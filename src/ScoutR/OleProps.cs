using System.Runtime.InteropServices;

namespace ScoutR;

/// <summary>
/// Reads and writes a property straight in the file's OLE property sets (Inventor files are OLE compound
/// files; iProperties live there), without Inventor or Apprentice — the same way DSOFile does it.
/// </summary>
internal static class OleProps
{
    /// <summary>Inventor's "Design Tracking Properties" set; property 5 is Part Number.</summary>
    public static readonly Guid DesignTracking = new("32853F0F-3444-11D1-9E93-0060B03C1CA6");
    public const uint PartNumberId = 5;

    private const uint StgmRead = 0x0, StgmReadWrite = 0x2, StgmShareExclusive = 0x10, StgmShareDenyWrite = 0x20;
    private const uint StgfmtAny = 4;
    private const ushort VtLpstr = 30, VtLpwstr = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropSpec { public uint Kind; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr P; public IntPtr P2; }

    [ComImport, Guid("0000013A-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertySetStorage
    {
        [PreserveSig] int Create(ref Guid fmtid, IntPtr clsid, uint flags, uint mode, out IPropertyStorage stg);
        [PreserveSig] int Open(ref Guid fmtid, uint mode, out IPropertyStorage stg);
    }

    [ComImport, Guid("00000138-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStorage
    {
        [PreserveSig] int ReadMultiple(uint count, [In] PropSpec[] specs, [Out] PropVariant[] values);
        [PreserveSig] int WriteMultiple(uint count, [In] PropSpec[] specs, [In] PropVariant[] values, uint firstId);
        [PreserveSig] int DeleteMultiple(uint count, [In] PropSpec[] specs);
        [PreserveSig] int ReadPropertyNames(uint count, [In] uint[] ids, [Out] IntPtr[] names);
        [PreserveSig] int WritePropertyNames(uint count, [In] uint[] ids, [In] IntPtr[] names);
        [PreserveSig] int DeletePropertyNames(uint count, [In] uint[] ids);
        [PreserveSig] int Commit(uint flags);
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgOpenStorageEx(string name, uint mode, uint format, uint attrs, IntPtr options,
        IntPtr reserved, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertySetStorage obj);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    private static IPropertySetStorage OpenFile(string path, bool write)
    {
        var iid = typeof(IPropertySetStorage).GUID;
        int hr = StgOpenStorageEx(path, write ? StgmReadWrite | StgmShareExclusive : StgmRead | StgmShareDenyWrite,
            StgfmtAny, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out var pss);
        Marshal.ThrowExceptionForHR(hr);
        return pss;
    }

    /// <summary>The text value of a property, or null if missing.</summary>
    public static string? Read(string path, Guid set, uint id)
    {
        var pss = OpenFile(path, write: false);
        try
        {
            var fmtid = set;
            if (pss.Open(ref fmtid, StgmRead | StgmShareExclusive, out var ps) != 0) return null;
            try
            {
                var spec = new[] { new PropSpec { Kind = 1, Data = (IntPtr)id } };
                var values = new PropVariant[1];
                if (ps.ReadMultiple(1, spec, values) != 0) return null;
                try
                {
                    return values[0].Vt switch
                    {
                        VtLpwstr => Marshal.PtrToStringUni(values[0].P),
                        VtLpstr => Marshal.PtrToStringAnsi(values[0].P),
                        _ => null,
                    };
                }
                finally
                {
                    PropVariantClear(ref values[0]);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(ps);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(pss);
        }
    }

    /// <summary>Writes a text property and commits it to the file.</summary>
    public static void Write(string path, Guid set, uint id, string value)
    {
        var pss = OpenFile(path, write: true);
        try
        {
            var fmtid = set;
            Marshal.ThrowExceptionForHR(pss.Open(ref fmtid, StgmReadWrite | StgmShareExclusive, out var ps));
            try
            {
                var spec = new[] { new PropSpec { Kind = 1, Data = (IntPtr)id } };
                var pv = new PropVariant { Vt = VtLpwstr, P = Marshal.StringToCoTaskMemUni(value) };
                try
                {
                    Marshal.ThrowExceptionForHR(ps.WriteMultiple(1, spec, new[] { pv }, 2));
                    Marshal.ThrowExceptionForHR(ps.Commit(0));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pv.P);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(ps);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(pss);
        }
    }
}
