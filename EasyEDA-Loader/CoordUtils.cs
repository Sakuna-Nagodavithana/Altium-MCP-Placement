using EDP;
using System;

namespace EasyEDA_Loader
{
    public static class CoordUtils
    {
        public static double CoordToMils(int coord) => Utils.CoordToMils(coord);

        public static double CoordToMm(int coord) => Utils.CoordToMMs(coord);
    }
}
