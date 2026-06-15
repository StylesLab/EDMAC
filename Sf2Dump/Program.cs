    using System.Text;
    using System.Text.Json;

    if (args.Length == 0)
    {
        Console.WriteLine("Usage: Sf2Dump path/to/file.sf2");
        return;
    }

    var sf2 = Sf2Reader.Read(args[0]);

    var json = JsonSerializer.Serialize(sf2, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    Console.WriteLine(json);
    File.WriteAllText("output.json", json);

    public sealed record Sf2Metadata(
        Dictionary<string, string> Info,
        List<Sf2Preset> Presets,
        List<Sf2Instrument> Instruments,
        List<Sf2Sample> Samples
    );

    public sealed record Sf2Preset(
        string Name,
        int Bank,
        int Program,
        List<Sf2Zone> Zones
    );

    public sealed record Sf2Instrument(
        string Name,
        List<Sf2Zone> Zones
    );

    public sealed record Sf2Zone(
        int? KeyLow,
        int? KeyHigh,
        int? VelLow,
        int? VelHigh,
        string? Instrument,
        string? Sample,
        Dictionary<string, int> Generators
    );

    public sealed record Sf2Sample(
        string Name,
        uint Start,
        uint End,
        uint LoopStart,
        uint LoopEnd,
        uint SampleRate,
        byte OriginalPitch,
        sbyte PitchCorrection,
        ushort SampleLink,
        ushort SampleType
    );

    public static class Sf2Reader
    {
        public static Sf2Metadata Read(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.ASCII);

            Expect(br, "RIFF");
            _ = br.ReadUInt32();
            Expect(br, "sfbk");

            var info = new Dictionary<string, string>();
            var chunks = new Dictionary<string, byte[]>();

            while (fs.Position < fs.Length)
            {
                var id = ReadId(br);
                var size = br.ReadUInt32();
                var start = fs.Position;

                if (id == "LIST")
                {
                    var listType = ReadId(br);
                    var payloadSize = size - 4;

                    if (listType == "INFO")
                        ReadInfoList(br, payloadSize, info);
                    else if (listType == "pdta")
                        ReadPdtaList(br, payloadSize, chunks);
                    else
                        fs.Position += payloadSize;
                }
                else
                {
                    fs.Position += size;
                }

                if ((size & 1) == 1)
                    fs.Position++;

                fs.Position = start + size + (size & 1);
            }

            var phdr = ReadPhdr(chunks["phdr"]);
            var pbag = ReadBag(chunks["pbag"]);
            var pgen = ReadGen(chunks["pgen"]);

            var inst = ReadInst(chunks["inst"]);
            var ibag = ReadBag(chunks["ibag"]);
            var igen = ReadGen(chunks["igen"]);

            var shdr = ReadShdr(chunks["shdr"]);

            var samples = shdr
                .Take(shdr.Count - 1)
                .Select(s => new Sf2Sample(
                    s.Name,
                    s.Start,
                    s.End,
                    s.StartLoop,
                    s.EndLoop,
                    s.SampleRate,
                    s.OriginalPitch,
                    s.PitchCorrection,
                    s.SampleLink,
                    s.SampleType))
                .ToList();

            var instruments = new List<Sf2Instrument>();

            for (int i = 0; i < inst.Count - 1; i++)
            {
                var zones = BuildZones(
                    ibag,
                    igen,
                    inst[i].BagIndex,
                    inst[i + 1].BagIndex,
                    instNames: null,
                    sampleNames: shdr.Select(s => s.Name).ToList());

                instruments.Add(new Sf2Instrument(inst[i].Name, zones));
            }

            var instrumentNames = inst.Select(x => x.Name).ToList();

            var presets = new List<Sf2Preset>();

            for (int i = 0; i < phdr.Count - 1; i++)
            {
                var zones = BuildZones(
                    pbag,
                    pgen,
                    phdr[i].BagIndex,
                    phdr[i + 1].BagIndex,
                    instNames: instrumentNames,
                    sampleNames: null);

                presets.Add(new Sf2Preset(
                    phdr[i].Name,
                    phdr[i].Bank,
                    phdr[i].Preset,
                    zones));
            }

            return new Sf2Metadata(info, presets, instruments, samples);
        }

        private static List<Sf2Zone> BuildZones(
            List<Bag> bags,
            List<Gen> gens,
            int bagStart,
            int bagEnd,
            List<string>? instNames,
            List<string>? sampleNames)
        {
            var zones = new List<Sf2Zone>();

            for (int b = bagStart; b < bagEnd; b++)
            {
                int genStart = bags[b].GenIndex;
                int genEnd = bags[b + 1].GenIndex;

                int? keyLow = null;
                int? keyHigh = null;
                int? velLow = null;
                int? velHigh = null;
                string? instrument = null;
                string? sample = null;

                var generatorMap = new Dictionary<string, int>();

                for (int g = genStart; g < genEnd; g++)
                {
                    var gen = gens[g];
                    var name = GeneratorName(gen.Oper);
                    generatorMap[name] = gen.Amount;

                    if (gen.Oper == 43)
                    {
                        keyLow = gen.Amount & 0xFF;
                        keyHigh = (gen.Amount >> 8) & 0xFF;
                    }
                    else if (gen.Oper == 44)
                    {
                        velLow = gen.Amount & 0xFF;
                        velHigh = (gen.Amount >> 8) & 0xFF;
                    }
                    else if (gen.Oper == 41 && instNames != null)
                    {
                        instrument = SafeGet(instNames, gen.Amount);
                    }
                    else if (gen.Oper == 53 && sampleNames != null)
                    {
                        sample = SafeGet(sampleNames, gen.Amount);
                    }
                }

                zones.Add(new Sf2Zone(
                    keyLow,
                    keyHigh,
                    velLow,
                    velHigh,
                    instrument,
                    sample,
                    generatorMap));
            }

            return zones;
        }

        private static void ReadInfoList(
            BinaryReader br,
            uint size,
            Dictionary<string, string> info)
        {
            long end = br.BaseStream.Position + size;

            while (br.BaseStream.Position < end)
            {
                var id = ReadId(br);
                var chunkSize = br.ReadUInt32();
                var bytes = br.ReadBytes((int)chunkSize);

                var text = Encoding.ASCII.GetString(bytes)
                    .TrimEnd('\0', ' ', '\r', '\n');

                info[id] = text;

                if ((chunkSize & 1) == 1)
                    br.ReadByte();
            }
        }

        private static void ReadPdtaList(
            BinaryReader br,
            uint size,
            Dictionary<string, byte[]> chunks)
        {
            long end = br.BaseStream.Position + size;

            while (br.BaseStream.Position < end)
            {
                var id = ReadId(br);
                var chunkSize = br.ReadUInt32();
                chunks[id] = br.ReadBytes((int)chunkSize);

                if ((chunkSize & 1) == 1)
                    br.ReadByte();
            }
        }

        private static List<Phdr> ReadPhdr(byte[] data)
        {
            var list = new List<Phdr>();
            using var br = Reader(data);

            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                list.Add(new Phdr(
                    ReadFixedString(br, 20),
                    br.ReadUInt16(),
                    br.ReadUInt16(),
                    br.ReadUInt16()));
                br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt32();
            }

            return list;
        }

        private static List<Inst> ReadInst(byte[] data)
        {
            var list = new List<Inst>();
            using var br = Reader(data);

            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                list.Add(new Inst(
                    ReadFixedString(br, 20),
                    br.ReadUInt16()));
            }

            return list;
        }

        private static List<Bag> ReadBag(byte[] data)
        {
            var list = new List<Bag>();
            using var br = Reader(data);

            while (br.BaseStream.Position < br.BaseStream.Length)
                list.Add(new Bag(br.ReadUInt16(), br.ReadUInt16()));

            return list;
        }

        private static List<Gen> ReadGen(byte[] data)
        {
            var list = new List<Gen>();
            using var br = Reader(data);

            while (br.BaseStream.Position < br.BaseStream.Length)
                list.Add(new Gen(br.ReadUInt16(), br.ReadInt16()));

            return list;
        }

        private static List<Shdr> ReadShdr(byte[] data)
        {
            var list = new List<Shdr>();
            using var br = Reader(data);

            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                list.Add(new Shdr(
                    ReadFixedString(br, 20),
                    br.ReadUInt32(),
                    br.ReadUInt32(),
                    br.ReadUInt32(),
                    br.ReadUInt32(),
                    br.ReadUInt32(),
                    br.ReadByte(),
                    br.ReadSByte(),
                    br.ReadUInt16(),
                    br.ReadUInt16()));
            }

            return list;
        }

        private static BinaryReader Reader(byte[] data) =>
            new(new MemoryStream(data), Encoding.ASCII);

        private static string ReadId(BinaryReader br) =>
            Encoding.ASCII.GetString(br.ReadBytes(4));

        private static void Expect(BinaryReader br, string expected)
        {
            var actual = ReadId(br);
            if (actual != expected)
                throw new InvalidDataException($"Expected {expected}, got {actual}");
        }

        private static string ReadFixedString(BinaryReader br, int length)
        {
            var bytes = br.ReadBytes(length);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
        }

        private static string SafeGet(List<string> list, int index) =>
            index >= 0 && index < list.Count ? list[index] : $"#{index}";

        private static string GeneratorName(int oper) => oper switch
        {
            0 => "startAddrsOffset",
            1 => "endAddrsOffset",
            2 => "startloopAddrsOffset",
            3 => "endloopAddrsOffset",
            8 => "initialFilterFc",
            9 => "initialFilterQ",
            17 => "pan",
            34 => "attackVolEnv",
            35 => "holdVolEnv",
            36 => "decayVolEnv",
            37 => "sustainVolEnv",
            38 => "releaseVolEnv",
            41 => "instrument",
            43 => "keyRange",
            44 => "velRange",
            48 => "initialAttenuation",
            51 => "coarseTune",
            52 => "fineTune",
            53 => "sampleID",
            54 => "sampleModes",
            56 => "scaleTuning",
            57 => "exclusiveClass",
            58 => "overridingRootKey",
            _ => $"generator_{oper}"
        };

        private sealed record Phdr(string Name, ushort Preset, ushort Bank, ushort BagIndex);
        private sealed record Inst(string Name, ushort BagIndex);
        private sealed record Bag(ushort GenIndex, ushort ModIndex);
        private sealed record Gen(ushort Oper, short Amount);

        private sealed record Shdr(
            string Name,
            uint Start,
            uint End,
            uint StartLoop,
            uint EndLoop,
            uint SampleRate,
            byte OriginalPitch,
            sbyte PitchCorrection,
            ushort SampleLink,
            ushort SampleType
        );
    }