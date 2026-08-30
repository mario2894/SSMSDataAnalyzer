using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// OeProbe — pure-metadata inspector for SSMS 22 assemblies.
// Uses System.Reflection.Metadata directly (no MetadataLoadContext, no dependency
// resolution, nothing executed). Safe to run against net472 GAC-less assemblies.
//
// Usage:
//   OeProbe types    <asm...>  --ns <substr> [--all]   list defined types (+members)
//   OeProbe members  <asm...>  --type <substr>          dump members of matching types
//   OeProbe refs     <asm...>  --ns <substr>            list TypeRefs/MemberRefs consumed
//   OeProbe strings  <asm...>  --grep <substr>          user-string heap grep
//   OeProbe survey   <dir>     --ns <substr>            which files define/reference the ns
//   OeProbe il       <asm...>  --type <s> --method <s>   token-resolving IL disassembly
//   OeProbe res      <asm...>  --name <substr> --out <dir>  extract embedded resources

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("no verb"); return 1; }
        var verb = args[0];
        var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
        var opts = ParseOpts(args);

        try
        {
            switch (verb)
            {
                case "types": foreach (var f in Expand(files)) DumpTypes(f, opts); break;
                case "members": foreach (var f in Expand(files)) DumpMembers(f, opts); break;
                case "refs": foreach (var f in Expand(files)) DumpRefs(f, opts); break;
                case "strings": foreach (var f in Expand(files)) DumpStrings(f, opts); break;
                case "survey": Survey(files, opts); break;
                case "il": foreach (var f in Expand(files)) IlDump.Run(f, opts); break;
                case "res": foreach (var f in Expand(files)) ResDump.Run(f, opts); break;
                default: Console.Error.WriteLine("unknown verb " + verb); return 1;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
        return 0;
    }

    private static Dictionary<string, string> ParseOpts(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var k = args[i].Substring(2);
            var v = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
            d[k] = v;
        }
        return d;
    }

    private static IEnumerable<string> Expand(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            if (Directory.Exists(f))
                foreach (var x in Directory.EnumerateFiles(f, "*.dll", SearchOption.TopDirectoryOnly)) yield return x;
            else if (File.Exists(f)) yield return f;
            else Console.Error.WriteLine("!! missing " + f);
        }
    }

    private static bool Open(string path, out PEReader pe, out MetadataReader mr)
    {
        pe = null; mr = null;
        try
        {
            var fs = File.OpenRead(path);
            pe = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
            if (!pe.HasMetadata) { pe.Dispose(); pe = null; return false; }
            mr = pe.GetMetadataReader();
            return true;
        }
        catch { if (pe != null) pe.Dispose(); pe = null; return false; }
    }

    // ---------- types ----------
    private static void DumpTypes(string path, Dictionary<string, string> o)
    {
        if (!Open(path, out var pe, out var mr)) return;
        using (pe)
        {
            var nsFilter = o.TryGetValue("ns", out var n) ? n : null;
            bool all = o.ContainsKey("all");
            var asmName = mr.IsAssembly ? mr.GetString(mr.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(path);
            bool header = false;

            foreach (var h in mr.TypeDefinitions)
            {
                var td = mr.GetTypeDefinition(h);
                var ns = mr.GetString(td.Namespace);
                var name = mr.GetString(td.Name);
                var full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                if (nsFilter != null && full.IndexOf(nsFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var vis = Visibility(td.Attributes);
                if (!all && vis != "public" && vis != "nested public") continue;

                if (!header) { Console.WriteLine($"##### {Path.GetFileName(path)}  [{asmName}]"); header = true; }
                var kind = (td.Attributes & TypeAttributes.Interface) != 0 ? "interface"
                    : IsEnum(mr, td) ? "enum"
                    : (td.Attributes & TypeAttributes.Abstract) != 0 && (td.Attributes & TypeAttributes.Sealed) != 0 ? "static class"
                    : (td.Attributes & TypeAttributes.Abstract) != 0 ? "abstract class"
                    : (td.Attributes & TypeAttributes.Sealed) != 0 ? "sealed class" : "class";
                var baseName = BaseName(mr, td);
                var ifaces = Interfaces(mr, td);
                Console.WriteLine($"  {vis} {kind} {full}{(baseName != null ? " : " + baseName : "")}{(ifaces.Count > 0 ? (baseName != null ? ", " : " : ") + string.Join(", ", ifaces) : "")}");
                if (o.ContainsKey("members")) DumpTypeMembers(mr, td, "      ", all);
            }
        }
    }

    private static bool IsEnum(MetadataReader mr, TypeDefinition td)
    {
        var b = BaseName(mr, td);
        return b == "System.Enum";
    }

    private static string Visibility(TypeAttributes a)
    {
        switch (a & TypeAttributes.VisibilityMask)
        {
            case TypeAttributes.Public: return "public";
            case TypeAttributes.NotPublic: return "internal";
            case TypeAttributes.NestedPublic: return "nested public";
            case TypeAttributes.NestedFamily: return "nested protected";
            case TypeAttributes.NestedAssembly: return "nested internal";
            case TypeAttributes.NestedPrivate: return "nested private";
            case TypeAttributes.NestedFamORAssem: return "nested protected internal";
            case TypeAttributes.NestedFamANDAssem: return "nested private protected";
            default: return "?";
        }
    }

    private static string BaseName(MetadataReader mr, TypeDefinition td)
    {
        if (td.BaseType.IsNil) return null;
        return HandleName(mr, td.BaseType);
    }

    private static List<string> Interfaces(MetadataReader mr, TypeDefinition td)
    {
        var list = new List<string>();
        foreach (var ih in td.GetInterfaceImplementations())
        {
            var ii = mr.GetInterfaceImplementation(ih);
            list.Add(HandleName(mr, ii.Interface));
        }
        return list;
    }

    private static string HandleName(MetadataReader mr, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
                {
                    var t = mr.GetTypeDefinition((TypeDefinitionHandle)h);
                    var ns = mr.GetString(t.Namespace);
                    return string.IsNullOrEmpty(ns) ? mr.GetString(t.Name) : ns + "." + mr.GetString(t.Name);
                }
            case HandleKind.TypeReference:
                {
                    var t = mr.GetTypeReference((TypeReferenceHandle)h);
                    var ns = mr.GetString(t.Namespace);
                    return string.IsNullOrEmpty(ns) ? mr.GetString(t.Name) : ns + "." + mr.GetString(t.Name);
                }
            case HandleKind.TypeSpecification:
                {
                    var ts = mr.GetTypeSpecification((TypeSpecificationHandle)h);
                    try { return ts.DecodeSignature(new Sig(mr), null); } catch { return "<typespec>"; }
                }
            default: return "<" + h.Kind + ">";
        }
    }

    // ---------- members ----------
    private static void DumpMembers(string path, Dictionary<string, string> o)
    {
        if (!Open(path, out var pe, out var mr)) return;
        using (pe)
        {
            var filter = o.TryGetValue("type", out var t) ? t : null;
            bool all = o.ContainsKey("all");
            bool header = false;
            foreach (var h in mr.TypeDefinitions)
            {
                var td = mr.GetTypeDefinition(h);
                var ns = mr.GetString(td.Namespace);
                var name = mr.GetString(td.Name);
                var full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                if (filter != null && full.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!header) { Console.WriteLine($"##### {Path.GetFileName(path)}"); header = true; }
                var ifaces = Interfaces(mr, td);
                Console.WriteLine($"  {Visibility(td.Attributes)} {full}"
                    + (BaseName(mr, td) != null ? " : " + BaseName(mr, td) : "")
                    + (ifaces.Count > 0 ? "   [implements " + string.Join(", ", ifaces) + "]" : ""));
                DumpTypeMembers(mr, td, "      ", all);
            }
        }
    }

    private static void DumpTypeMembers(MetadataReader mr, TypeDefinition td, string ind, bool all)
    {
        foreach (var fh in td.GetFields())
        {
            var fd = mr.GetFieldDefinition(fh);
            var acc = fd.Attributes & FieldAttributes.FieldAccessMask;
            if (!all && acc != FieldAttributes.Public) continue;
            string type;
            try { type = fd.DecodeSignature(new Sig(mr), null); } catch { type = "?"; }
            var fstat = ((fd.Attributes & FieldAttributes.Static) != 0) ? "static " : "";
            string cval = "";
            try {
                var ch = fd.GetDefaultValue();
                if (!ch.IsNil) {
                    var c = mr.GetConstant(ch);
                    var br = mr.GetBlobReader(c.Value);
                    switch (c.TypeCode) {
                        case ConstantTypeCode.Int32: cval = " = " + br.ReadInt32(); break;
                        case ConstantTypeCode.UInt32: cval = " = " + br.ReadUInt32(); break;
                        case ConstantTypeCode.Int16: cval = " = " + br.ReadInt16(); break;
                        case ConstantTypeCode.String: cval = " = \"" + br.ReadUTF16(br.Length) + "\""; break;
                        case ConstantTypeCode.Boolean: cval = " = " + br.ReadBoolean(); break;
                        default: cval = " = <" + c.TypeCode + ">"; break;
                    }
                }
            } catch {}
            Console.WriteLine($"{ind}[F] {AccName(acc)} {fstat}{type} {mr.GetString(fd.Name)}{cval}");
        }
        var propAccessors = new HashSet<int>();
        foreach (var ph in td.GetProperties())
        {
            var pd = mr.GetPropertyDefinition(ph);
            var acc = pd.GetAccessors();
            if (!acc.Getter.IsNil) propAccessors.Add(MetadataTokens.GetRowNumber(acc.Getter));
            if (!acc.Setter.IsNil) propAccessors.Add(MetadataTokens.GetRowNumber(acc.Setter));
            MethodAttributes ma = default;
            if (!acc.Getter.IsNil) ma = mr.GetMethodDefinition(acc.Getter).Attributes;
            else if (!acc.Setter.IsNil) ma = mr.GetMethodDefinition(acc.Setter).Attributes;
            var mAcc = ma & MethodAttributes.MemberAccessMask;
            if (!all && mAcc != MethodAttributes.Public) continue;
            string sig;
            try { sig = pd.DecodeSignature(new Sig(mr), null).ReturnType; } catch { sig = "?"; }
            var ga = (acc.Getter.IsNil ? "" : "get; ") + (acc.Setter.IsNil ? "" : "set; ");
            Console.WriteLine($"{ind}[P] {AccName(mAcc)} {sig} {mr.GetString(pd.Name)} [ {ga}]");
        }
        foreach (var eh in td.GetEvents())
        {
            var ed = mr.GetEventDefinition(eh);
            var a = ed.GetAccessors();
            if (!a.Adder.IsNil) propAccessors.Add(MetadataTokens.GetRowNumber(a.Adder));
            if (!a.Remover.IsNil) propAccessors.Add(MetadataTokens.GetRowNumber(a.Remover));
            MethodAttributes ma = a.Adder.IsNil ? default : mr.GetMethodDefinition(a.Adder).Attributes;
            var mAcc = ma & MethodAttributes.MemberAccessMask;
            if (!all && mAcc != MethodAttributes.Public) continue;
            Console.WriteLine($"{ind}[E] {AccName(mAcc)} {HandleName(mr, ed.Type)} {mr.GetString(ed.Name)}");
        }
        foreach (var mh in td.GetMethods())
        {
            if (propAccessors.Contains(MetadataTokens.GetRowNumber(mh))) continue;
            var md = mr.GetMethodDefinition(mh);
            var acc = md.Attributes & MethodAttributes.MemberAccessMask;
            if (!all && acc != MethodAttributes.Public) continue;
            string sig;
            try
            {
                var s = md.DecodeSignature(new Sig(mr), null);
                sig = $"{s.ReturnType} {mr.GetString(md.Name)}({string.Join(", ", s.ParameterTypes)})";
            }
            catch { sig = mr.GetString(md.Name) + "(?)"; }
            Console.WriteLine($"{ind}[M] {AccName(acc)} {((md.Attributes & MethodAttributes.Static) != 0 ? "static " : "")}{sig}");
        }
    }

    private static string AccName(FieldAttributes a) => a switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Private => "private",
        FieldAttributes.Family => "protected",
        FieldAttributes.Assembly => "internal",
        FieldAttributes.FamORAssem => "protected internal",
        FieldAttributes.FamANDAssem => "private protected",
        _ => "?"
    };

    private static string AccName(MethodAttributes a) => a switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Private => "private",
        MethodAttributes.Family => "protected",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.FamANDAssem => "private protected",
        _ => "?"
    };

    // ---------- refs (what this assembly consumes) ----------
    private static void DumpRefs(string path, Dictionary<string, string> o)
    {
        if (!Open(path, out var pe, out var mr)) return;
        using (pe)
        {
            var nsFilter = o.TryGetValue("ns", out var n) ? n : null;
            var seen = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var h in mr.MemberReferences)
            {
                var m = mr.GetMemberReference(h);
                var parent = HandleName(mr, m.Parent);
                if (nsFilter != null && parent.IndexOf(nsFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string sig = "";
                try
                {
                    if (m.GetKind() == MemberReferenceKind.Method)
                    {
                        var s = m.DecodeMethodSignature(new Sig(mr), null);
                        sig = $"{s.ReturnType} {mr.GetString(m.Name)}({string.Join(", ", s.ParameterTypes)})";
                    }
                    else sig = m.DecodeFieldSignature(new Sig(mr), null) + " " + mr.GetString(m.Name);
                }
                catch { sig = mr.GetString(m.Name); }
                seen.Add(parent + " :: " + sig);
            }
            if (seen.Count > 0)
            {
                Console.WriteLine($"##### {Path.GetFileName(path)} consumes:");
                foreach (var s in seen) Console.WriteLine("  " + s);
            }
        }
    }

    private static void DumpStrings(string path, Dictionary<string, string> o)
    {
        if (!Open(path, out var pe, out var mr)) return;
        using (pe)
        {
            var grep = o.TryGetValue("grep", out var g) ? g : null;
            bool header = false;
            foreach (var mh in mr.MethodDefinitions)
            {
                var md = mr.GetMethodDefinition(mh);
                if (md.RelativeVirtualAddress == 0) continue;
                MethodBodyBlock body;
                try { body = pe.GetMethodBody(md.RelativeVirtualAddress); } catch { continue; }
                var il = body.GetILBytes();
                if (il == null) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x72) continue; // ldstr
                    int tok = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                    if ((tok & 0xFF000000) != 0x70000000) continue;
                    string s2;
                    try { s2 = mr.GetUserString(MetadataTokens.UserStringHandle(tok & 0x00FFFFFF)); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(s2)) continue;
                    if (grep != null && s2.IndexOf(grep, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!header) { Console.WriteLine($"##### {Path.GetFileName(path)}"); header = true; }
                    Console.WriteLine("  [" + mr.GetString(md.Name) + "] " + s2.Replace((char)13, ' ').Replace((char)10, ' '));
                }
            }
        }
    }

    // ---------- survey ----------
    private static void Survey(List<string> roots, Dictionary<string, string> o)
    {
        var nsFilter = o.TryGetValue("ns", out var n) ? n : "ObjectExplorer";
        foreach (var f in Expand(roots))
        {
            if (!Open(f, out var pe, out var mr)) continue;
            using (pe)
            {
                int defs = 0, refs = 0;
                foreach (var h in mr.TypeDefinitions)
                {
                    var td = mr.GetTypeDefinition(h);
                    if ((mr.GetString(td.Namespace) + "." + mr.GetString(td.Name)).IndexOf(nsFilter, StringComparison.OrdinalIgnoreCase) >= 0) defs++;
                }
                foreach (var h in mr.TypeReferences)
                {
                    var tr = mr.GetTypeReference(h);
                    if ((mr.GetString(tr.Namespace) + "." + mr.GetString(tr.Name)).IndexOf(nsFilter, StringComparison.OrdinalIgnoreCase) >= 0) refs++;
                }
                if (defs > 0 || refs > 0)
                    Console.WriteLine($"{Path.GetFileName(f),-70} defs={defs,-5} refs={refs}");
            }
        }
    }
}

// Minimal signature -> string provider.
internal sealed class Sig : ISignatureTypeProvider<string, object>, ICustomAttributeTypeProvider<string>
{
    private readonly MetadataReader _mr;
    public Sig(MetadataReader mr) { _mr = mr; }

    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object gc, int i) => "!!" + i;
    public string GetGenericTypeParameter(object gc, int i) => "!" + i;
    public string GetModifiedType(string mod, string unmod, bool isRequired) => unmod;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.TypedReference => "typedref",
        PrimitiveTypeCode.Void => "void",
        _ => c.ToString()
    };
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawKind)
    {
        var t = r.GetTypeDefinition(h);
        var ns = r.GetString(t.Namespace);
        return string.IsNullOrEmpty(ns) ? r.GetString(t.Name) : ns + "." + r.GetString(t.Name);
    }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawKind)
    {
        var t = r.GetTypeReference(h);
        var ns = r.GetString(t.Namespace);
        return string.IsNullOrEmpty(ns) ? r.GetString(t.Name) : ns + "." + r.GetString(t.Name);
    }
    public string GetTypeFromSpecification(MetadataReader r, object gc, TypeSpecificationHandle h, byte rawKind)
        => r.GetTypeSpecification(h).DecodeSignature(this, gc);
    public string GetSystemType() => "System.Type";
    public bool IsSystemType(string type) => type == "System.Type";
    public string GetTypeFromSerializedName(string name) => name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
}
