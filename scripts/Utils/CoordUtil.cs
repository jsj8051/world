using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using World.Utils.H3;

namespace World.Utils
{
    public static class CoordUtil
    {
        // Lat 是 纬度
        public static Vector3 LatLngToSphere(LatLng g, float radius)
        {
            // g.Lat / g.Lng 均为弧度
            double x = Math.Cos(g.Lat) * Math.Cos(g.Lng);
            double y = Math.Sin(g.Lat);                       // 纬度直接决定 Y（北）
            double z = Math.Cos(g.Lat) * Math.Sin(g.Lng);
            return new Vector3((float)x, (float)y, (float)z) * radius;
        }
    }
}