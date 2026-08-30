using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Extract embedded manifest resources. SSMS keeps the Object Explorer hierarchy
// definitions (sqlexplorerhier.xml etc.) as embedded resources inside a .resources
// blob; --raw dumps the whole resource, --list just names them.
internal static class ResDump
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
            var nameFilter = o.TryGetValue("name", out var n) ? n : null;
            var outDir = o.TryGetValue("out", out var d) ? d : null;
            var resDir = pe.PEHeaders.CorHeader.ResourcesDirectory;

            foreach (var h in mr.ManifestResources)
            {
                var res = mr.GetManifestResource(h);
                var name = mr.GetString(res.Name);
                if (!res.Implementation.IsNil) { Console.WriteLine("  (external) " + name); continue; }
                var offset = (int)res.Offset;
                var section = pe.GetSectionData(resDir.RelativeVirtualAddress);
                var reader = section.GetReader(offset, 4);
                int len = reader.ReadInt32();
                Console.WriteLine($"  {name}  ({len} bytes)");
                if (nameFilter == null || name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var data = section.GetReader(offset + 4, len).ReadBytes(len);
                if (outDir != null)
                {
                    Directory.CreateDirectory(outDir);
                    var file = Path.Combine(outDir, name);
                    File.WriteAllBytes(file, data);
                    Console.WriteLine("    -> " + file);
                }
            }
        }
    }
}
