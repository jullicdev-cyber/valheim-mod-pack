using System;
using System.IO;
using System.Text;

namespace ValheimModPack.PartyPrison
{
    internal static class ProtocolTests
    {
        private const string Account = "Steam_76561198000000001";
        private static int assertions;

        private static void Check(bool value, string description)
        { ++assertions; if (!value) throw new Exception("FAIL: " + description); }

        private static void Reject(Action action, string description)
        {
            ++assertions;
            try { action(); }
            catch (ArgumentException) { return; }
            catch (InvalidDataException) { return; }
            catch (InvalidOperationException) { return; }
            catch (IOException) { return; }
            throw new Exception("FAIL: accepted " + description);
        }

        private static byte[] Write(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            { write(writer); writer.Flush(); return stream.ToArray(); }
        }

        private static void Read(byte[] bytes, Action<BinaryReader> read)
        {
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            { read(reader); PrisonProtocol.End(reader); }
        }

        private static PrisonRegion Region()
        {
            return new PrisonRegion { Center = new PrisonPoint(1024.123456789, 30.500000001, -9999.987654321), Radius = 18.125, HalfHeight = 8.25,
                CellSpawn = new PrisonPoint(1017.123456789, 28.500000001, -9999.987654321),
                ArenaSpawn = new PrisonPoint(1031.123456789, 28.500000001, -9999.987654321) };
        }

        private static SentenceState Sentence()
        {
            return new SentenceState { AccountId = Account, SentenceId = "3b121908cf214951a4231d7069a1df52", PlayerName = "Заключённый 🛡",
                Reason = "Нарушение правил пати", RemainingSeconds = 1800.125000001, Revision = 4294967301L,
                ReturnPosition = new PrisonPoint(-9000.012345678, -12.000000003, 4242.987654321) };
        }

        public static int Main()
        {
            try
            {
                Headers(); RoundTrip(); TextBounds(); BlobBounds(); Positions(); Regions(); Sentences(); CompleteMessages(); ActivityMessages();
                Console.WriteLine("PartyPrison protocol codec checks passed: " + assertions);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void Headers()
        {
            const long world = -98765432123456789L;
            for (int kind = PrisonProtocol.State; kind <= PrisonProtocol.AdmissionFailed; ++kind)
            {
                int expected = kind;
                byte[] bytes = Write(delegate(BinaryWriter writer) { PrisonProtocol.WriteHeader(writer, world, expected); });
                Check(bytes.Length == 16, "canonical header size");
                Read(bytes, delegate(BinaryReader reader) { Check(PrisonProtocol.ReadHeader(reader, world) == expected && reader.BaseStream.Position == 16, "known message kind and signed world round-trip"); });
            }
            byte[] valid = Write(delegate(BinaryWriter writer) { PrisonProtocol.WriteHeader(writer, world, PrisonProtocol.State); });
            Truncate(valid, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); }, "envelope header");
            Check(BitConverter.ToInt32(valid, 0) == 4, "wave progress snapshots use protocol version four");
            foreach (int version in new[] { 0, -1, 1, 2, 3, PrisonProtocol.Version + 1, Int32.MaxValue })
            {
                byte[] wrong = (byte[])valid.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(version), 0, wrong, 0, 4);
                Reject(delegate { Read(wrong, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); }); }, "unknown protocol version");
            }
            foreach (long identity in new[] { 0L, 123L, -1L })
            {
                byte[] wrong = (byte[])valid.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(identity), 0, wrong, 4, 8);
                Reject(delegate { Read(wrong, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); }); }, "different or uninitialized packet world");
            }
            Reject(delegate { Read(valid, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, 0); }); }, "uninitialized expected world");
            foreach (int kind in new[] { -1, 0, 15, Int32.MaxValue })
            {
                byte[] wrong = (byte[])valid.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(kind), 0, wrong, 12, 4);
                Reject(delegate { Read(wrong, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); }); }, "unknown incoming message kind");
                int wrongKind = kind;
                Reject(delegate { Write(delegate(BinaryWriter writer) { PrisonProtocol.WriteHeader(writer, world, wrongKind); }); }, "unknown outgoing message kind");
            }
            Reject(delegate { Write(delegate(BinaryWriter writer) { PrisonProtocol.WriteHeader(writer, 0, PrisonProtocol.State); }); }, "outgoing zero world");
            Reject(delegate { Write(delegate(BinaryWriter writer) { writer.Write((byte)0); PrisonProtocol.WriteHeader(writer, world, PrisonProtocol.State); }); }, "header written after payload begins");
            Reject(delegate { Read(valid, delegate(BinaryReader reader) { reader.ReadByte(); PrisonProtocol.ReadHeader(reader, world); }); }, "header parsed after payload begins");
            var oversized = new byte[PrisonProtocol.MaximumBytes + 1]; Buffer.BlockCopy(valid, 0, oversized, 0, valid.Length);
            Reject(delegate { Read(oversized, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); }); }, "oversized envelope rejected before body parsing");
            var maximum = new byte[PrisonProtocol.MaximumBytes]; Buffer.BlockCopy(valid, 0, maximum, 0, valid.Length);
            Read(maximum, delegate(BinaryReader reader)
            {
                Check(PrisonProtocol.ReadHeader(reader, world) == PrisonProtocol.State, "maximum envelope size is permitted");
                reader.ReadBytes(maximum.Length - 16);
            });
        }

        private static void RoundTrip()
        {
            PrisonRegion expectedRegion = Region(); SentenceState expectedSentence = Sentence();
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Region(writer, expectedRegion); PrisonProtocol.Sentence(writer, expectedSentence); }), delegate(BinaryReader reader)
            {
                PrisonRegion region = PrisonProtocol.Region(reader); SentenceState sentence = PrisonProtocol.Sentence(reader);
                Check(region.Center.X == expectedRegion.Center.X && region.Center.Y == expectedRegion.Center.Y && region.Center.Z == expectedRegion.Center.Z,
                    "region retains exact double coordinates");
                Check(region.Radius == expectedRegion.Radius && region.HalfHeight == expectedRegion.HalfHeight && region.CellSpawn.Y == expectedRegion.CellSpawn.Y
                    && region.ArenaSpawn.X == expectedRegion.ArenaSpawn.X, "region bounds and both activity anchors round-trip");
                Check(sentence.AccountId == expectedSentence.AccountId && sentence.SentenceId == expectedSentence.SentenceId && sentence.PlayerName == expectedSentence.PlayerName
                    && sentence.Reason == expectedSentence.Reason, "Unicode sentence labels and authenticated account round-trip");
                Check(sentence.Revision == expectedSentence.Revision && sentence.RemainingSeconds == expectedSentence.RemainingSeconds && !sentence.PendingRelease,
                    "64-bit revision and fractional active time round-trip");
                Check(sentence.ReturnPosition.X == expectedSentence.ReturnPosition.X && sentence.ReturnPosition.Y == expectedSentence.ReturnPosition.Y
                    && sentence.ReturnPosition.Z == expectedSentence.ReturnPosition.Z, "return destination retains exact double coordinates");
            });
            SentenceState pending = Sentence(); pending.RemainingSeconds = 0; pending.PendingRelease = true;
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Sentence(writer, pending); }), delegate(BinaryReader reader)
            { var decoded = PrisonProtocol.Sentence(reader); Check(decoded.PendingRelease && decoded.RemainingSeconds == 0 && decoded.SentenceId == pending.SentenceId, "pending release token is retained"); });
            pending.EmergencyRelease = true;
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Sentence(writer, pending); }), delegate(BinaryReader reader)
            { var decoded = PrisonProtocol.Sentence(reader); Check(decoded.PendingRelease && decoded.EmergencyRelease && decoded.RemainingSeconds == 0 && decoded.SentenceId == pending.SentenceId, "emergency release bypass flag retains its sentence identity"); });
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Region(writer, null); PrisonProtocol.Sentence(writer, null); }), delegate(BinaryReader reader)
            { Check(PrisonProtocol.Region(reader) == null && PrisonProtocol.Sentence(reader) == null, "free player / unconfigured region null flags round-trip"); });
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Text(writer, null); PrisonProtocol.Text(writer, ""); }), delegate(BinaryReader reader)
            { Check(PrisonProtocol.Text(reader) == "" && PrisonProtocol.Text(reader) == "", "empty heartbeat tokens are canonical empty strings"); });
        }

        private static void TextBounds()
        {
            string maximum = new string('x', 1024);
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Text(writer, maximum); }), delegate(BinaryReader reader)
            { Check(PrisonProtocol.Text(reader) == maximum, "maximum byte length accepted"); });
            Reject(delegate { Write(delegate(BinaryWriter writer) { PrisonProtocol.Text(writer, maximum + "x"); }); }, "oversized outgoing ASCII text");
            Reject(delegate { Write(delegate(BinaryWriter writer) { PrisonProtocol.Text(writer, new string('Ж', 513)); }); }, "multibyte outgoing text is bounded by bytes");
            Reject(delegate { Write(delegate(BinaryWriter writer) { PrisonProtocol.Text(writer, "\ud800"); }); }, "malformed outgoing surrogate");
            foreach (int count in new[] { -1, 1025, Int32.MaxValue })
            {
                int value = count; byte[] bytes = Write(delegate(BinaryWriter writer) { writer.Write(value); writer.Write(new byte[2]); });
                Reject(delegate { Read(bytes, delegate(BinaryReader reader) { PrisonProtocol.Text(reader); }); }, "advertised invalid text length");
            }
            Reject(delegate { Read(Write(delegate(BinaryWriter writer) { writer.Write(8); writer.Write(new byte[7]); }), delegate(BinaryReader reader) { PrisonProtocol.Text(reader); }); }, "advertised text exceeds remaining payload");
            Reject(delegate { Read(Write(delegate(BinaryWriter writer) { writer.Write(2); writer.Write(new byte[] { 0xc0, 0xaf }); }), delegate(BinaryReader reader) { PrisonProtocol.Text(reader); }); }, "noncanonical overlong UTF-8");
            Reject(delegate { Read(Write(delegate(BinaryWriter writer) { writer.Write(3); writer.Write(new byte[] { 0xed, 0xa0, 0x80 }); }), delegate(BinaryReader reader) { PrisonProtocol.Text(reader); }); }, "UTF-8 encoded lone surrogate");
            byte[] valid = Write(delegate(BinaryWriter writer) { PrisonProtocol.Text(writer, "Игрок 🛡"); });
            Truncate(valid, delegate(BinaryReader reader) { PrisonProtocol.Text(reader); }, "UTF-8 text");
            Reject(delegate { Read(Append(valid, 0), delegate(BinaryReader reader) { PrisonProtocol.Text(reader); }); }, "text trailing bytes");
        }

        private static void BlobBounds()
        {
            Check(PrisonProtocol.MaximumBlobBytes == 4 * 1024 * 1024 && PrisonProtocol.MaximumBytes == PrisonProtocol.MaximumBlobBytes + 8192,
                "custody payload and full message limits agree with four MiB inventory plus envelope allowance");
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Blob(writer, new byte[0]); }), delegate(BinaryReader reader)
            { Check(PrisonProtocol.Blob(reader).Length == 0, "zero-length inventory blob is canonical"); });
            var maximum = new byte[PrisonProtocol.MaximumBlobBytes];
            for (int i = 0; i < maximum.Length; ++i) maximum[i] = (byte)((i * 31 + 17) & 255);
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Blob(writer, maximum); }), delegate(BinaryReader reader)
            { Check(EqualBytes(maximum, PrisonProtocol.Blob(reader)), "maximum inventory blob retains every byte"); });
            Reject(delegate { Write(delegate(BinaryWriter writer) { PrisonProtocol.Blob(writer, new byte[PrisonProtocol.MaximumBlobBytes + 1]); }); }, "oversized outgoing inventory blob");
            Reject(delegate { Write(delegate(BinaryWriter writer) { PrisonProtocol.Blob(writer, null); }); }, "null outgoing inventory blob");
            foreach (int count in new[] { -1, Int32.MinValue, PrisonProtocol.MaximumBlobBytes + 1, Int32.MaxValue })
            {
                int invalid = count;
                Reject(delegate { Read(Write(delegate(BinaryWriter writer) { writer.Write(invalid); writer.Write(new byte[8]); }),
                    delegate(BinaryReader reader) { PrisonProtocol.Blob(reader); }); }, "negative or excessive advertised inventory length");
            }
            Reject(delegate { Read(Write(delegate(BinaryWriter writer) { writer.Write(16); writer.Write(new byte[15]); }),
                delegate(BinaryReader reader) { PrisonProtocol.Blob(reader); }); }, "inventory blob advertises more bytes than remain");
            Reject(delegate { Read(Write(delegate(BinaryWriter writer) { writer.Write(PrisonProtocol.MaximumBlobBytes); writer.Write(new byte[1]); }),
                delegate(BinaryReader reader) { PrisonProtocol.Blob(reader); }); }, "maximum advertised blob with a truncated payload is rejected before allocation");
            byte[] small = Write(delegate(BinaryWriter writer) { PrisonProtocol.Blob(writer, new byte[] { 0, 1, 128, 255, 18, 73, 95 }); });
            Truncate(small, delegate(BinaryReader reader) { PrisonProtocol.Blob(reader); }, "inventory blob");
            Reject(delegate { Read(Append(small, 0), delegate(BinaryReader reader) { PrisonProtocol.Blob(reader); }); }, "inventory blob trailing bytes");
            byte[] offer = Write(delegate(BinaryWriter writer) {
                PrisonProtocol.WriteHeader(writer, 99, PrisonProtocol.InventoryOffer);
                PrisonProtocol.Text(writer, Sentence().SentenceId); PrisonProtocol.Blob(writer, maximum);
            });
            Check(offer.Length <= PrisonProtocol.MaximumBytes, "maximum custody offer fits the complete message envelope");
            Read(offer, delegate(BinaryReader reader) {
                Check(PrisonProtocol.ReadHeader(reader, 99) == PrisonProtocol.InventoryOffer
                    && PrisonProtocol.Text(reader) == Sentence().SentenceId && EqualBytes(maximum, PrisonProtocol.Blob(reader)),
                    "maximum custody offer validates header, sentence token and exact inventory bytes together");
            });
        }

        private static bool EqualBytes(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; ++i) if (first[i] != second[i]) return false;
            return true;
        }

        private static void Positions()
        {
            PrisonPoint point = new PrisonPoint(0.000000001, -999999.999999999, 999999.999999999);
            Read(Write(delegate(BinaryWriter writer) { PrisonProtocol.Point(writer, point); }), delegate(BinaryReader reader)
            { var decoded = PrisonProtocol.Point(reader); Check(decoded.X == point.X && decoded.Y == point.Y && decoded.Z == point.Z, "point codec does not narrow doubles to floats"); });
            foreach (double value in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, 1000000.001, -1000000.001 })
            {
                double invalid = value;
                for (int coordinate = 0; coordinate < 3; ++coordinate)
                {
                    PrisonPoint wrong = new PrisonPoint();
                    if (coordinate == 0) wrong.X = invalid; else if (coordinate == 1) wrong.Y = invalid; else wrong.Z = invalid;
                    byte[] bytes = Write(delegate(BinaryWriter writer) { PrisonProtocol.Point(writer, wrong); });
                    Reject(delegate { Read(bytes, delegate(BinaryReader reader) { PrisonProtocol.Point(reader); }); }, "nonfinite or out-of-range point axis");
                }
            }
            byte[] encoded = Write(delegate(BinaryWriter writer) { PrisonProtocol.Point(writer, point); });
            Truncate(encoded, delegate(BinaryReader reader) { PrisonProtocol.Point(reader); }, "point");
        }

        private static void Regions()
        {
            byte[] encoded = Write(delegate(BinaryWriter writer) { PrisonProtocol.Region(writer, Region()); });
            Truncate(encoded, delegate(BinaryReader reader) { PrisonProtocol.Region(reader); }, "region");
            Reject(delegate { Read(new byte[] { 2 }, delegate(BinaryReader reader) { PrisonProtocol.Region(reader); }); }, "noncanonical region presence flag");
            foreach (double size in new[] { Double.NaN, Double.PositiveInfinity, -1, 5.999, 100.001 })
            {
                PrisonRegion wrong = Region(); wrong.Radius = size;
                byte[] bytes = Write(delegate(BinaryWriter writer) { PrisonProtocol.Region(writer, wrong); });
                Reject(delegate { Read(bytes, delegate(BinaryReader reader) { PrisonProtocol.Region(reader); }); }, "invalid region radius");
            }
            foreach (double size in new[] { Double.NaN, Double.NegativeInfinity, -1, 3.999, 100.001 })
            {
                PrisonRegion wrong = Region(); wrong.HalfHeight = size;
                byte[] bytes = Write(delegate(BinaryWriter writer) { PrisonProtocol.Region(writer, wrong); });
                Reject(delegate { Read(bytes, delegate(BinaryReader reader) { PrisonProtocol.Region(reader); }); }, "invalid region halfheight");
            }
            var cellOutside = Region(); cellOutside.CellSpawn.X += 100;
            RejectRegion(cellOutside, "cell spawn outside jail");
            var arenaOutside = Region(); arenaOutside.ArenaSpawn.Y += 100;
            RejectRegion(arenaOutside, "arena spawn outside jail");
            var centerNonfinite = Region(); centerNonfinite.Center.Z = Double.NaN;
            RejectRegion(centerNonfinite, "NaN center");
            Reject(delegate { Read(Append(encoded, 0), delegate(BinaryReader reader) { PrisonProtocol.Region(reader); }); }, "region trailing bytes");
        }

        private static void RejectRegion(PrisonRegion value, string description)
        {
            byte[] bytes = Write(delegate(BinaryWriter writer) { PrisonProtocol.Region(writer, value); });
            Reject(delegate { Read(bytes, delegate(BinaryReader reader) { PrisonProtocol.Region(reader); }); }, description);
        }

        private static void Sentences()
        {
            byte[] encoded = Write(delegate(BinaryWriter writer) { PrisonProtocol.Sentence(writer, Sentence()); });
            Truncate(encoded, delegate(BinaryReader reader) { PrisonProtocol.Sentence(reader); }, "sentence");
            Reject(delegate { Read(new byte[] { 2 }, delegate(BinaryReader reader) { PrisonProtocol.Sentence(reader); }); }, "noncanonical sentence presence flag");
            foreach (string identity in new[] { "", "Заключённый", "local-host", "Steam_7656119800000000x" })
            {
                var wrong = Sentence(); wrong.AccountId = identity; RejectSentence(wrong, "unauthenticated account representation");
            }
            foreach (string token in new[] { "", "wrong", "00000000000000000000000000000000", "3B121908CF214951A4231D7069A1DF52" })
            {
                var wrong = Sentence(); wrong.SentenceId = token; RejectSentence(wrong, "invalid or noncanonical sentence token");
            }
            foreach (double time in new[] { Double.NaN, Double.PositiveInfinity, -1, SentencePolicy.MaximumDurationSeconds + 1 })
            {
                var wrong = Sentence(); wrong.RemainingSeconds = time; RejectSentence(wrong, "invalid remaining time");
            }
            var zeroActive = Sentence(); zeroActive.RemainingSeconds = 0; RejectSentence(zeroActive, "zero active time without release intent");
            var timedRelease = Sentence(); timedRelease.PendingRelease = true; RejectSentence(timedRelease, "pending release with positive time");
            var activeEmergency = Sentence(); activeEmergency.EmergencyRelease = true; RejectSentence(activeEmergency, "emergency bypass without pending release");
            foreach (long revision in new[] { 0L, -1L, Int64.MinValue })
            {
                var wrong = Sentence(); wrong.Revision = revision; RejectSentence(wrong, "invalid revision");
            }
            var longName = Sentence(); longName.PlayerName = new string('x', 101); RejectSentence(longName, "oversized player label");
            var longReason = Sentence(); longReason.Reason = new string('x', 301); RejectSentence(longReason, "oversized punishment reason");
            var controls = Sentence(); controls.Reason = "reason\r\nspoof"; RejectSentence(controls, "sentence text control characters");
            var invalidReturn = Sentence(); invalidReturn.ReturnPosition.Z = Double.NegativeInfinity; RejectSentence(invalidReturn, "nonfinite return destination");
            byte[] noncanonicalPending = (byte[])encoded.Clone(); noncanonicalPending[noncanonicalPending.Length - 10] = 2;
            Reject(delegate { Read(noncanonicalPending, delegate(BinaryReader reader) { PrisonProtocol.Sentence(reader); }); }, "noncanonical pending-release wire boolean");
            byte[] noncanonicalEmergency = (byte[])encoded.Clone(); noncanonicalEmergency[noncanonicalEmergency.Length - 1] = 2;
            Reject(delegate { Read(noncanonicalEmergency, delegate(BinaryReader reader) { PrisonProtocol.Sentence(reader); }); }, "noncanonical emergency-release wire boolean");
            Reject(delegate { Read(Append(encoded, 0), delegate(BinaryReader reader) { PrisonProtocol.Sentence(reader); }); }, "sentence trailing bytes");
        }

        private static void RejectSentence(SentenceState value, string description)
        {
            byte[] bytes = Write(delegate(BinaryWriter writer) { PrisonProtocol.Sentence(writer, value); });
            Reject(delegate { Read(bytes, delegate(BinaryReader reader) { PrisonProtocol.Sentence(reader); }); }, description);
        }

        private static void CompleteMessages()
        {
            // The host/client dispatcher stages these values locally, then calls End
            // before assigning state. Exercise that complete codec boundary, including
            // a valid region followed by a malformed sentence and trailing garbage.
            byte[] valid = Write(delegate(BinaryWriter writer) {
                writer.Write(99L); PrisonProtocol.Region(writer, Region()); PrisonProtocol.Sentence(writer, Sentence());
                writer.Write(4); writer.Write(1); PrisonProtocol.Text(writer, Account); PrisonProtocol.Text(writer, Sentence().SentenceId);
                PrisonProtocol.Text(writer, new string('a', 64)); PrisonProtocol.Text(writer, "Ожидайте сохранения вещей");
                writer.Write(5); writer.Write(2); PrisonProtocol.Text(writer, Sentence().SentenceId); writer.Write(3);
            });
            Truncate(valid, ReadStateFields, "complete state body");
            Reject(delegate { Read(Append(valid, 0), ReadStateFields); }, "complete state body with trailing garbage");
            var malformed = Sentence(); malformed.PendingRelease = true;
            byte[] body = Write(delegate(BinaryWriter writer) {
                writer.Write(100L); PrisonProtocol.Region(writer, Region()); PrisonProtocol.Sentence(writer, malformed);
                writer.Write(4); writer.Write(1); PrisonProtocol.Text(writer, Account); PrisonProtocol.Text(writer, Sentence().SentenceId);
                PrisonProtocol.Text(writer, new string('a', 64)); PrisonProtocol.Text(writer, "Ожидайте сохранения вещей");
                writer.Write(5); writer.Write(2); PrisonProtocol.Text(writer, Sentence().SentenceId); writer.Write(3);
            });
            Reject(delegate { Read(body, ReadStateFields); }, "valid region followed by inconsistent sentence");
            Read(valid, delegate(BinaryReader reader)
            { Check(reader.ReadInt64() == 99 && PrisonProtocol.Region(reader).Radius == 18.125 && PrisonProtocol.Sentence(reader).AccountId == Account
                    && reader.ReadInt32() == 4 && reader.ReadInt32() == 1 && PrisonProtocol.Text(reader) == Account && PrisonProtocol.Text(reader) == Sentence().SentenceId
                    && PrisonProtocol.Text(reader) == new string('a', 64) && PrisonProtocol.Text(reader) == "Ожидайте сохранения вещей"
                    && reader.ReadInt32() == 5 && reader.ReadInt32() == 2 && PrisonProtocol.Text(reader) == Sentence().SentenceId && reader.ReadInt32() == 3,
                "whole valid state stages custody, cell loadout, difficulty and generated gear revision together"); });
            Read(Write(delegate(BinaryWriter writer) {
                writer.Write(101L); PrisonProtocol.Region(writer, null); PrisonProtocol.Sentence(writer, null);
                writer.Write(0); writer.Write(-1); PrisonProtocol.Text(writer, ""); PrisonProtocol.Text(writer, ""); PrisonProtocol.Text(writer, ""); PrisonProtocol.Text(writer, "");
                writer.Write(0); writer.Write(0); PrisonProtocol.Text(writer, ""); writer.Write(0);
            }), delegate(BinaryReader reader) {
                Check(reader.ReadInt64() == 101 && PrisonProtocol.Region(reader) == null && PrisonProtocol.Sentence(reader) == null
                    && reader.ReadInt32() == 0 && reader.ReadInt32() == -1 && PrisonProtocol.Text(reader) == "" && PrisonProtocol.Text(reader) == ""
                    && PrisonProtocol.Text(reader) == "" && PrisonProtocol.Text(reader) == ""
                    && reader.ReadInt32() == 0 && reader.ReadInt32() == 0 && PrisonProtocol.Text(reader) == "" && reader.ReadInt32() == 0,
                    "absent sentence carries explicit absent custody stage and empty recovery fields");
            });
            Read(Write(delegate(BinaryWriter writer) {
                writer.Write(102L); PrisonProtocol.Region(writer, Region()); PrisonProtocol.Sentence(writer, null);
                writer.Write(4); writer.Write(4); PrisonProtocol.Text(writer, Account); PrisonProtocol.Text(writer, Sentence().SentenceId);
                PrisonProtocol.Text(writer, new string('a', 64)); PrisonProtocol.Text(writer, "");
                writer.Write(5); writer.Write(2); PrisonProtocol.Text(writer, ""); writer.Write(0);
            }), delegate(BinaryReader reader) {
                Check(reader.ReadInt64() == 102 && PrisonProtocol.Region(reader) != null && PrisonProtocol.Sentence(reader) == null
                    && reader.ReadInt32() == 4 && reader.ReadInt32() == 4 && PrisonProtocol.Text(reader) == Account
                    && PrisonProtocol.Text(reader) == Sentence().SentenceId && PrisonProtocol.Text(reader) == new string('a', 64)
                    && PrisonProtocol.Text(reader) == "" && reader.ReadInt32() == 5 && reader.ReadInt32() == 2 && PrisonProtocol.Text(reader) == "" && reader.ReadInt32() == 0,
                    "released custody identity survives while public gear expiry is explicitly signalled");
            });
        }

        private static void ReadStateFields(BinaryReader reader)
        { reader.ReadInt64(); PrisonProtocol.Region(reader); PrisonProtocol.Sentence(reader); reader.ReadInt32(); reader.ReadInt32();
            PrisonProtocol.Text(reader); PrisonProtocol.Text(reader); PrisonProtocol.Text(reader); PrisonProtocol.Text(reader);
            reader.ReadInt32(); reader.ReadInt32(); PrisonProtocol.Text(reader); reader.ReadInt32(); }

        private static void ActivityMessages()
        {
            const long world = 99;
            string token = Sentence().SentenceId;
            for (int family = 0; family < 6; ++family)
                for (int difficulty = 0; difficulty < 3; ++difficulty) {
                    int selectedFamily = family, selectedDifficulty = difficulty;
                    byte[] choice = Write(delegate(BinaryWriter writer) {
                        PrisonProtocol.WriteHeader(writer, world, PrisonProtocol.CombatChoice);
                        PrisonProtocol.Text(writer, token); writer.Write(selectedFamily); writer.Write(selectedDifficulty);
                    });
                    Read(choice, delegate(BinaryReader reader) {
                        Check(PrisonProtocol.ReadHeader(reader, world) == PrisonProtocol.CombatChoice && PrisonProtocol.Text(reader) == token
                            && reader.ReadInt32() == selectedFamily && reader.ReadInt32() == selectedDifficulty, "all eighteen cell combat choices fit canonical wire shape");
                    });
                    Truncate(choice, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); PrisonProtocol.Text(reader); reader.ReadInt32(); reader.ReadInt32(); }, "combat choice");
                    Reject(delegate { Read(Append(choice, 0), delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); PrisonProtocol.Text(reader); reader.ReadInt32(); reader.ReadInt32(); }); }, "combat choice trailing bytes");
                }
            byte[] defeat = Write(delegate(BinaryWriter writer) {
                PrisonProtocol.WriteHeader(writer, world, PrisonProtocol.Defeat); PrisonProtocol.Text(writer, token);
            });
            Read(defeat, delegate(BinaryReader reader) { Check(PrisonProtocol.ReadHeader(reader, world) == PrisonProtocol.Defeat && PrisonProtocol.Text(reader) == token, "defeat message identifies the current sentence"); });
            Truncate(defeat, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); PrisonProtocol.Text(reader); }, "defeat notification");
            byte[] failed = Write(delegate(BinaryWriter writer) {
                PrisonProtocol.WriteHeader(writer, world, PrisonProtocol.AdmissionFailed); PrisonProtocol.Text(writer, token); PrisonProtocol.Text(writer, "Не завершена передача вещей");
            });
            Read(failed, delegate(BinaryReader reader) { Check(PrisonProtocol.ReadHeader(reader, world) == PrisonProtocol.AdmissionFailed
                && PrisonProtocol.Text(reader) == token && PrisonProtocol.Text(reader) == "Не завершена передача вещей", "bounded admission watchdog reports failure for its current token"); });
            Truncate(failed, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); PrisonProtocol.Text(reader); PrisonProtocol.Text(reader); }, "admission failure");
            foreach (bool receipt in new[] { false, true }) {
                byte[] ack = Write(delegate(BinaryWriter writer) {
                    PrisonProtocol.WriteHeader(writer, world, PrisonProtocol.ReleaseAck); PrisonProtocol.Text(writer, token); writer.Write(4294967301L); writer.Write(receipt);
                });
                Read(ack, delegate(BinaryReader reader) { Check(PrisonProtocol.ReadHeader(reader, world) == PrisonProtocol.ReleaseAck && PrisonProtocol.Text(reader) == token
                    && reader.ReadInt64() == 4294967301L && PrisonProtocol.Flag(reader) == receipt, "release acknowledgement retains durable sequence and custody receipt evidence"); });
                Truncate(ack, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); PrisonProtocol.Text(reader); reader.ReadInt64(); PrisonProtocol.Flag(reader); }, "release receipt acknowledgement");
                ack[ack.Length - 1] = 2;
                Reject(delegate { Read(ack, delegate(BinaryReader reader) { PrisonProtocol.ReadHeader(reader, world); PrisonProtocol.Text(reader); reader.ReadInt64(); PrisonProtocol.Flag(reader); }); }, "noncanonical release receipt evidence");
            }
        }

        private static void Truncate(byte[] valid, Action<BinaryReader> decode, string description)
        {
            for (int i = 0; i < valid.Length; ++i)
            {
                byte[] prefix = new byte[i]; Buffer.BlockCopy(valid, 0, prefix, 0, i);
                Reject(delegate { Read(prefix, decode); }, description + " truncated to " + i);
            }
        }

        private static byte[] Append(byte[] source, byte value)
        { var result = new byte[source.Length + 1]; Buffer.BlockCopy(source, 0, result, 0, source.Length); result[result.Length - 1] = value; return result; }
    }
}
