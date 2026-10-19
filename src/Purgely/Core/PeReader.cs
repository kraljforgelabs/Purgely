using System.IO;
using System.Text;

namespace Purgely.Core;

/// <summary>Minimal reader for Windows executables: CPU type and imported DLL names.</summary>
public static class PeReader
{
    public sealed record PeInfo(ushort Machine, List<string> Imports)
    {
        public string Architecture => Machine switch
        {
            0x8664 => "x64",
            0x014C => "x86",
            0xAA64 => "arm64",
            _ => "unknown",
        };
    }

    public static PeInfo? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return null; // "MZ"

            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 256) return null;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return null; // "PE\0\0"

            var machine = br.ReadUInt16();
            var sectionCount = br.ReadUInt16();
            fs.Position += 12;
            var optionalSize = br.ReadUInt16();
            fs.Position += 2;

            var optionalStart = fs.Position;
            var isPe32Plus = br.ReadUInt16() == 0x20B;
            fs.Position = optionalStart + (isPe32Plus ? 112 : 96) + 8; // data directory #1 = imports
            var importRva = br.ReadUInt32();

            fs.Position = optionalStart + optionalSize;
            var sections = new List<(uint Va, uint Size, uint Raw)>();
            for (var i = 0; i < sectionCount && i < 96; i++)
            {
                fs.Position += 8; // name
                var virtualSize = br.ReadUInt32();
                var virtualAddress = br.ReadUInt32();
                var rawSize = br.ReadUInt32();
                var rawPointer = br.ReadUInt32();
                fs.Position += 16;
                sections.Add((virtualAddress, Math.Max(virtualSize, rawSize), rawPointer));
            }

            long ToOffset(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.Va && rva < s.Va + s.Size) return rva - s.Va + s.Raw;
                return -1;
            }

            var imports = new List<string>();
            var tableOffset = importRva == 0 ? -1 : ToOffset(importRva);
            if (tableOffset < 0) return new PeInfo(machine, imports);

            for (var i = 0; i < 1024; i++)
            {
                fs.Position = tableOffset + i * 20L;
                var originalFirstThunk = br.ReadUInt32();
                fs.Position += 8;
                var nameRva = br.ReadUInt32();
                var firstThunk = br.ReadUInt32();
                if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0) break;

                var nameOffset = ToOffset(nameRva);
                if (nameOffset < 0 || nameOffset >= fs.Length) continue;
                fs.Position = nameOffset;
                imports.Add(ReadAsciiZ(br).ToLowerInvariant());
            }
            return new PeInfo(machine, imports);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ReadAsciiZ(BinaryReader br)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 260; i++)
        {
            var b = br.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
