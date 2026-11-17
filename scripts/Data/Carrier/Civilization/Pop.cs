using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace world.scripts.Data.Carrier.Civilization
{
    public class Pop
    {
        public long Id { get; }
        public ulong CellId { get; }
        public int numberOfPeople { get; }
        public int cultureId { get; }
        public int religionId { get; }

    }
}