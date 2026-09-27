using System;
using System.IO;

namespace ValheimModPack.WorldCharacters
{
    public sealed class CharacterSession
    {
        public readonly string Token = Guid.NewGuid().ToString("N");
        public CharacterState State { get; private set; }
        public long LastSequence { get; private set; }
        public bool Loaded { get; private set; }
        public bool Closed { get; private set; }
        public CharacterSession(CharacterState state) { State = state.Copy(); }
        public void MarkLoaded(string token) { Check(token); Loaded = true; }
        public CharacterState Next(string token, long sequence, CharacterState update)
        {
            Check(token);
            if (!Loaded || sequence != LastSequence + 1) throw new InvalidDataException("Unexpected snapshot sequence or unloaded character.");
            if (update.World != State.World || update.Character != State.Character || update.Owner != State.Owner)
                throw new InvalidDataException("Snapshot identity mismatch.");
            var next = update.Copy(); next.Revision = checked(State.Revision + 1); next.Name = State.Name;
            StateCodec.Validate(next); return next;
        }
        public void Committed(CharacterState next, long sequence, bool final)
        {
            if (Closed || next.Revision != State.Revision + 1 || sequence != LastSequence + 1) throw new InvalidDataException("Commit order mismatch.");
            State = next.Copy(); LastSequence = sequence; Closed = final;
        }
        private void Check(string token) { if (Closed || token != Token) throw new InvalidDataException("Expired or invalid session token."); }
    }
}
