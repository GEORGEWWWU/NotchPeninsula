using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace NotchPeninsula
{
    // The compositor consumes a D2D geometry. No D2D render target or bitmap is needed.
    [WinRTExposedType(typeof(AcrylicClipGeometry.InterfaceDetails))]
    internal sealed class AcrylicClipGeometry(float left, float top, float width, float height, IslandOutline outline)
        : IGeometrySource2D
    {
        internal int GetGeometry(out nint geometry)
        {
            var iid = new Guid("06152247-6F50-465A-9245-118BFD3B6007");
            int hr = D2D1CreateFactory(1, in iid, 0, out nint factory); // multithreaded
            geometry = 0;
            if (hr < 0) return hr;
            try { return TryGetGeometryUsingFactory(factory, out geometry); }
            finally { Marshal.Release(factory); }
        }

        internal unsafe int TryGetGeometryUsingFactory(nint factory, out nint geometry)
        {
            geometry = 0;
            if (factory == 0) return unchecked((int)0x80070057); // E_INVALIDARG
            var create = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)factory)[10]; // ID2D1Factory::CreatePathGeometry
            nint path;
            int hr = create(factory, &path);
            if (hr < 0) return hr;
            geometry = path;

            var open = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)geometry)[17]; // ID2D1PathGeometry::Open
            nint sink;
            hr = open(geometry, &sink);
            if (hr >= 0)
            {
                try { hr = WriteOutline(sink); }
                finally { Marshal.Release(sink); }
            }
            if (hr < 0)
            {
                Marshal.Release(geometry);
                geometry = 0;
            }
            return hr;
        }

        private unsafe int WriteOutline(nint sink)
        {
            var methods = *(nint**)sink;
            var begin = (delegate* unmanaged[Stdcall]<nint, Vector2, int, void>)methods[5];
            var line = (delegate* unmanaged[Stdcall]<nint, Vector2, void>)methods[10];
            var arc = (delegate* unmanaged[Stdcall]<nint, Arc*, void>)methods[14];
            var end = (delegate* unmanaged[Stdcall]<nint, int, void>)methods[8];
            var close = (delegate* unmanaged[Stdcall]<nint, int>)methods[9];

            float right = left + width, bottom = top + height;
            var (rx, ry, rb) = outline;
            begin(sink, new(left + rx, top), 0); // filled
            Corner(new(left, top + ry), Math.Abs(rx), ry, rx < 0);
            line(sink, new(left, bottom - rb));
            Corner(new(left + rb, bottom), rb, rb, false);
            line(sink, new(right - rb, bottom));
            Corner(new(right, bottom - rb), rb, rb, false);
            line(sink, new(right, top + ry));
            Corner(new(right - rx, top), Math.Abs(rx), ry, rx < 0);
            end(sink, 1); // closed
            return close(sink);

            void Corner(Vector2 endpoint, float x, float y, bool clockwise)
            {
                if (x <= 0 || y <= 0)
                {
                    line(sink, endpoint);
                    return;
                }
                var segment = new Arc { Endpoint = endpoint, Radius = new(x, y), Sweep = clockwise ? 1 : 0 };
                arc(sink, &segment);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Arc
        {
            internal Vector2 Endpoint, Radius;
            internal float Rotation;
            internal int Sweep, Size;
        }

        [DllImport("d2d1.dll")]
        private static extern int D2D1CreateFactory(int type, in Guid iid, nint options, out nint factory);

        public sealed unsafe class InterfaceDetails : IWinRTExposedTypeDetails
        {
            private static readonly ComWrappers.ComInterfaceEntry[] Interfaces =
            [
                // IGeometrySource2D is an IInspectable marker interface with no methods of its own.
                new() { IID = new("CAFF7902-670C-4181-A624-DA977203B845"), Vtable = IInspectable.Vftbl.AbiToProjectionVftablePtr },
                new() { IID = new("0657AF73-53FD-47CF-84FF-C8492D2A80A3"), Vtable = CreateVtable() }
            ];

            public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces() => Interfaces;

            private static nint CreateVtable()
            {
                var table = (nint*)ComWrappersSupport.AllocateVtableMemory(typeof(InterfaceDetails), 5 * sizeof(nint));
                *(IUnknownVftbl*)table = IUnknownVftbl.AbiToProjectionVftbl;
                table[3] = (nint)(delegate* unmanaged[MemberFunction]<nint, nint*, int>)&GetGeometry;
                table[4] = (nint)(delegate* unmanaged[MemberFunction]<nint, nint, nint*, int>)&GetGeometryUsingFactory;
                return (nint)table;
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            private static int GetGeometry(nint self, nint* geometry)
            {
                *geometry = 0;
                try { return ComWrappersSupport.FindObject<AcrylicClipGeometry>(self).GetGeometry(out *geometry); }
                catch (Exception ex) { return ex.HResult; }
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
            private static int GetGeometryUsingFactory(nint self, nint factory, nint* geometry)
            {
                *geometry = 0;
                try { return ComWrappersSupport.FindObject<AcrylicClipGeometry>(self).TryGetGeometryUsingFactory(factory, out *geometry); }
                catch (Exception ex) { return ex.HResult; }
            }
        }
    }
}
