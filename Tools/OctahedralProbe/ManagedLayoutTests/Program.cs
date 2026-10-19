using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine.Rendering.HighDefinition;

static class Program
{
    static int Main(string[] args)
    {
        var cases = new List<object>();
        int passed = 0, failed = 0;
        void Test(string name, Action test)
        {
            try { test(); ++passed; cases.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { ++failed; cases.Add(new { name, passed = false, detail = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Test("valid nonzero source tile", () => new OctahedralProbeSourceLayout(128, 64, 64, 64, 4).ValidateTexture(128, 128, 8));
        Test("minimum two-level source tile", () => new OctahedralProbeSourceLayout(8, 8, 0, 0, 1).ValidateTexture(8, 8, 4));
        Test("default layout is rejected", () => Throws<ArgumentException>(() => default(OctahedralProbeSourceLayout).ValidateTexture(64, 64, 7)));
        Test("non power-of-two atlas is rejected", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(127, 64, 0, 0, 4)));
        Test("non power-of-two tile is rejected", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(128, 63, 0, 0, 3)));
        Test("out of bounds source region is rejected", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(128, 64, 128, 0, 4)));
        Test("unaligned source region is rejected", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(128, 64, 1, 0, 4)));
        Test("negative source region is rejected", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(128, 64, -64, 0, 4)));
        Test("coarsest mip must retain its interior", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(64, 64, 0, 0, 5)));
        Test("shift overflow cannot bypass validation", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(64, 64, 0, 0, 36)));
        Test("uninitialized source mip is rejected", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(64, 64, 0, 0, 4).ValidateTexture(64, 64, 4)));
        Test("mismatched texture dimensions are rejected", () => Throws<ArgumentException>(() => new OctahedralProbeSourceLayout(64, 64, 0, 0, 4).ValidateTexture(64, 128, 8)));
        if (args.Length > 0)
        {
            Directory.CreateDirectory(args[0]);
            File.WriteAllText(Path.Combine(args[0], "managed-layout-results.json"), JsonSerializer.Serialize(new { backend = ".NET, actual production layout source", passed, failed, cases }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
