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
        Test("publication before completion never exposes partial radiance", () => {
            var state = new ProbeFaceCompletion(); state.RequestPublication();
            for (int f=0; f<6; ++f) { Require(!state.canPublish); state.Record(f); }
            Require(state.canPublish);
        });
        Test("six faces wait for the scheduler publication step", () => {
            var state = new ProbeFaceCompletion(); for(int f=0;f<6;++f)state.Record(f);
            Require(state.complete && !state.canPublish); state.RequestPublication(); Require(state.canPublish);
        });
        Test("duplicate face execution cannot replace a missing face", () => {
            var state = new ProbeFaceCompletion(); state.RequestPublication();
            for(int f=0;f<5;++f)state.Record(f); for(int i=0;i<12;++i)state.Record(0); Require(!state.canPublish);
        });
        Test("all 720 interleavings publish only after six distinct faces", () => {
            int checkedOrders=0;
            void Visit(List<int> order) {
                if(order.Count==6) {
                    var state=new ProbeFaceCompletion(); state.RequestPublication();
                    for(int i=0;i<6;++i){Require(!state.canPublish);state.Record(order[i]);}Require(state.canPublish);++checkedOrders;return;
                }
                for(int f=0;f<6;++f)if(!order.Contains(f)){order.Add(f);Visit(order);order.RemoveAt(order.Count-1);}
            }
            Visit(new List<int>()); Require(checkedOrders==720);
        });
        Test("negative face is rejected", () => { var state=new ProbeFaceCompletion(); Throws<ArgumentOutOfRangeException>(()=>state.Record(-1)); });
        Test("face six is rejected", () => { var state=new ProbeFaceCompletion(); Throws<ArgumentOutOfRangeException>(()=>state.Record(6)); });
        Test("capture reset drops all completion and publication state", () => {
            var state=new ProbeFaceCompletion();state.RequestPublication();for(int f=0;f<6;++f)state.Record(f);
            state=default;Require(!state.complete&&!state.canPublish);state.Record(5);Require(!state.canPublish);
        });
        Test("interleaved probes own independent publication state", () => {
            var a=new ProbeFaceCompletion();var b=new ProbeFaceCompletion();a.RequestPublication();b.RequestPublication();
            for(int f=0;f<6;++f){a.Record(f);b.Record(0);}Require(a.canPublish&&!b.canPublish);
        });
        Test("rescheduled faces withdraw an older completion before publication", () => {
            var state=new ProbeFaceCompletion();for(int f=0;f<6;++f)state.Record(f);
            state.InvalidateFaces(63);state.RequestPublication();Require(!state.canPublish);
            for(int f=0;f<6;++f)state.Record(f);Require(state.canPublish);
        });
        Test("failed execution withdraws publication until the scheduler retries", () => {
            var state=new ProbeFaceCompletion();state.RequestPublication();for(int f=0;f<6;++f)state.Record(f);
            state.InvalidateFaces(1<<3);state.Record(3);Require(state.complete&&!state.canPublish);
            state.RequestPublication();Require(state.canPublish);
        });
        Test("invalid face masks cannot retain completion state", () => {
            var state=new ProbeFaceCompletion();Throws<ArgumentOutOfRangeException>(()=>state.InvalidateFaces(64));
        });
        if (args.Length > 0)
        {
            Directory.CreateDirectory(args[0]);
            File.WriteAllText(Path.Combine(args[0], "managed-layout-results.json"), JsonSerializer.Serialize(new { backend = ".NET, actual production layout and face-publication sources", passed, failed, cases }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Require(bool condition) { if (!condition) throw new Exception("Publication invariant failed"); }

    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
