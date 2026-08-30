using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Crude IL disassembler — enough to read control flow and, crucially, resolve
// ldstr / call / newobj tokens so we can see exactly which paths and APIs a
// method touches without running it.
internal static class IlDump
{
    public static void Run(string path, Dictionary<string, string> o)
    {
        FileStream fs;
        PEReader pe;
        MetadataReader mr;
        try
        {
            fs = File.OpenRead(path);
            pe = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
            if (!pe.HasMetadata) { pe.Dispose(); return; }
            mr = pe.GetMetadataReader();
        }
        catch { return; }

        using (pe)
        {
            var typeFilter = o.TryGetValue("type", out var t) ? t : null;
            var methFilter = o.TryGetValue("method", out var m) ? m : null;
            var grep = o.TryGetValue("grep", out var g) ? g : null;
            var headOnly = o.ContainsKey("headonly");
            foreach (var th in mr.TypeDefinitions)
            {
                var td = mr.GetTypeDefinition(th);
                var ns = mr.GetString(td.Namespace);
                var tn = mr.GetString(td.Name);
                var full = string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
                if (typeFilter != null && full.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var mh in td.GetMethods())
                {
                    var md = mr.GetMethodDefinition(mh);
                    var mn = mr.GetString(md.Name);
                    if (methFilter != null && mn.IndexOf(methFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (md.RelativeVirtualAddress == 0) continue;
                    MethodBodyBlock body;
                    try { body = pe.GetMethodBody(md.RelativeVirtualAddress); } catch { continue; }
                    var il = body.GetILBytes();
                    if (il == null) continue;
                    var sw = new StringWriter();
                    Print(mr, il, sw);
                    var text = sw.ToString();
                    if (grep != null && text.IndexOf(grep, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Console.WriteLine("##### " + Path.GetFileName(path) + " | " + full + "::" + mn);
                    if (!headOnly) Console.Write(text);
                }
            }
        }
    }

    private static void Print(MetadataReader mr, byte[] il, TextWriter w)
    {
        int i = 0;
        while (i < il.Length)
        {
            int off = i;
            int op = il[i++];
            if (op == 0xFE) { if (i >= il.Length) break; op = 0xFE00 | il[i++]; }
            int sz = OperandSize(op);
            string operand = "";
            if (sz == -1)
            {
                if (i + 4 > il.Length) break;
                int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; operand = "n=" + n;
            }
            else if (sz > 0)
            {
                if (i + sz > il.Length) break;
                if (sz == 4)
                {
                    int v = BitConverter.ToInt32(il, i);
                    if (op == 0x72)
                    {
                        try { operand = "\"" + mr.GetUserString(MetadataTokens.UserStringHandle(v & 0xFFFFFF)) + "\""; }
                        catch { operand = "str?"; }
                    }
                    else if (IsTokenOp(op)) operand = TokenName(mr, v);
                    else operand = v.ToString();
                }
                else if (sz == 8) operand = BitConverter.ToInt64(il, i).ToString();
                else if (sz == 2) operand = BitConverter.ToInt16(il, i).ToString();
                else operand = ((sbyte)il[i]).ToString();
                i += sz;
            }
            w.WriteLine("  IL_" + off.ToString("X4") + ": " + OpName(op).PadRight(12) + " " + operand);
        }
    }

    private static bool IsTokenOp(int op) =>
        op == 0x28 || op == 0x6F || op == 0x73 || op == 0x27 || op == 0xFE06 || op == 0xFE07 ||
        op == 0x7B || op == 0x7C || op == 0x7D || op == 0x7E || op == 0x7F || op == 0x80 ||
        op == 0x74 || op == 0x75 || op == 0x8C || op == 0xA5 || op == 0x71 || op == 0x81 ||
        op == 0x8D || op == 0xD0 || op == 0x79 || op == 0x8F || op == 0xC2 || op == 0xC6 ||
        op == 0x70 || op == 0xA3 || op == 0xA4;

    private static string TokenName(MetadataReader mr, int tok)
    {
        try
        {
            var h = MetadataTokens.EntityHandle(tok);
            switch (h.Kind)
            {
                case HandleKind.MemberReference:
                    {
                        var x = mr.GetMemberReference((MemberReferenceHandle)h);
                        return Name(mr, x.Parent) + "::" + mr.GetString(x.Name);
                    }
                case HandleKind.MethodDefinition:
                    {
                        var d = mr.GetMethodDefinition((MethodDefinitionHandle)h);
                        return Name(mr, d.GetDeclaringType()) + "::" + mr.GetString(d.Name);
                    }
                case HandleKind.FieldDefinition:
                    {
                        var f = mr.GetFieldDefinition((FieldDefinitionHandle)h);
                        return Name(mr, f.GetDeclaringType()) + "::" + mr.GetString(f.Name);
                    }
                case HandleKind.MethodSpecification:
                    {
                        var ms = mr.GetMethodSpecification((MethodSpecificationHandle)h);
                        return TokenName(mr, MetadataTokens.GetToken(ms.Method));
                    }
                default: return Name(mr, h);
            }
        }
        catch { return "tok:" + tok.ToString("X8"); }
    }

    private static string Name(MetadataReader mr, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
                {
                    var t = mr.GetTypeDefinition((TypeDefinitionHandle)h);
                    var ns = mr.GetString(t.Namespace);
                    return (string.IsNullOrEmpty(ns) ? "" : ns + ".") + mr.GetString(t.Name);
                }
            case HandleKind.TypeReference:
                {
                    var t = mr.GetTypeReference((TypeReferenceHandle)h);
                    var ns = mr.GetString(t.Namespace);
                    return (string.IsNullOrEmpty(ns) ? "" : ns + ".") + mr.GetString(t.Name);
                }
            default: return h.Kind.ToString();
        }
    }

    private static int OperandSize(int op)
    {
        if (op == 0x45) return -1;
        switch (op)
        {
            case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13:
            case 0x1F: case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F:
            case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
            case 0x36: case 0x37: case 0xDE:
                return 1;
            case 0x21: case 0x23:
                return 8;
            case 0x22:
                return 4;
            case 0xFE09: case 0xFE0A: case 0xFE0B: case 0xFE0C: case 0xFE0D: case 0xFE0E:
                return 2;
            case 0x20: case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D:
            case 0x3E: case 0x3F: case 0x40: case 0x41: case 0x42: case 0x43: case 0x44:
            case 0x27: case 0x28: case 0x6F: case 0x70: case 0x71: case 0x72: case 0x73:
            case 0x74: case 0x75: case 0x79: case 0x7B: case 0x7C: case 0x7D: case 0x7E:
            case 0x7F: case 0x80: case 0x81: case 0x8C: case 0x8D: case 0x8F: case 0xA3:
            case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xD0:
            case 0xFE06: case 0xFE07: case 0xFE15: case 0xFE16: case 0xFE1C:
                return 4;
            default:
                return 0;
        }
    }

    private static string OpName(int op)
    {
        switch (op)
        {
            case 0x00: return "nop";
            case 0x01: return "break";
            case 0x02: return "ldarg.0";
            case 0x03: return "ldarg.1";
            case 0x04: return "ldarg.2";
            case 0x05: return "ldarg.3";
            case 0x06: return "ldloc.0";
            case 0x07: return "ldloc.1";
            case 0x08: return "ldloc.2";
            case 0x09: return "ldloc.3";
            case 0x0A: return "stloc.0";
            case 0x0B: return "stloc.1";
            case 0x0C: return "stloc.2";
            case 0x0D: return "stloc.3";
            case 0x0E: return "ldarg.s";
            case 0x0F: return "ldarga.s";
            case 0x10: return "starg.s";
            case 0x11: return "ldloc.s";
            case 0x12: return "ldloca.s";
            case 0x13: return "stloc.s";
            case 0x14: return "ldnull";
            case 0x15: return "ldc.i4.m1";
            case 0x16: return "ldc.i4.0";
            case 0x17: return "ldc.i4.1";
            case 0x18: return "ldc.i4.2";
            case 0x19: return "ldc.i4.3";
            case 0x1A: return "ldc.i4.4";
            case 0x1B: return "ldc.i4.5";
            case 0x1C: return "ldc.i4.6";
            case 0x1D: return "ldc.i4.7";
            case 0x1E: return "ldc.i4.8";
            case 0x1F: return "ldc.i4.s";
            case 0x20: return "ldc.i4";
            case 0x25: return "dup";
            case 0x26: return "pop";
            case 0x28: return "call";
            case 0x2A: return "ret";
            case 0x2B: return "br.s";
            case 0x2C: return "brfalse.s";
            case 0x2D: return "brtrue.s";
            case 0x2E: return "beq.s";
            case 0x38: return "br";
            case 0x39: return "brfalse";
            case 0x3A: return "brtrue";
            case 0x6F: return "callvirt";
            case 0x71: return "ldobj";
            case 0x72: return "ldstr";
            case 0x73: return "newobj";
            case 0x74: return "castclass";
            case 0x75: return "isinst";
            case 0x79: return "unbox";
            case 0x7B: return "ldfld";
            case 0x7C: return "ldflda";
            case 0x7D: return "stfld";
            case 0x7E: return "ldsfld";
            case 0x7F: return "ldsflda";
            case 0x80: return "stsfld";
            case 0x8C: return "box";
            case 0x8D: return "newarr";
            case 0x8E: return "ldlen";
            case 0x9A: return "ldelem.ref";
            case 0xA2: return "stelem.ref";
            case 0xA5: return "unbox.any";
            case 0xD0: return "ldtoken";
            case 0xDE: return "leave.s";
            case 0xDD: return "leave";
            case 0xFE06: return "ldftn";
            case 0xFE07: return "ldvirtftn";
            case 0xFE01: return "ceq";
            case 0xFE15: return "initobj";
            case 0xFE16: return "constrained";
            case 0xFE1C: return "sizeof";
            default: return "op_" + op.ToString("X2");
        }
    }
}
