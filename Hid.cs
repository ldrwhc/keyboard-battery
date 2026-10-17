using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal sealed class Device {
    public string Path, Name;
    public ushort UsagePage, Usage, Input, Output;
    public override string ToString() { return Name + " page=" + UsagePage.ToString("X4") + " usage=" + Usage.ToString("X4") + " in=" + Input + " out=" + Output + "\r\n" + Path; }
}
internal sealed class Battery {
    public int Level;
    public bool Charging;
}
internal static class Hid {
    [StructLayout(LayoutKind.Sequential)] struct Interface { public int Size; public Guid Class; public int Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct Caps {
        public ushort Usage, Page, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort Link, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid guid, uint index, ref Interface data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref Interface data, IntPtr detail, uint size, out uint needed, IntPtr dev);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] value, int length);
    [DllImport("hid.dll")] static extern bool HidD_FlushQueue(SafeFileHandle handle);

    public static List<Device> Enumerate() {
        var result = new List<Device>();
        Guid guid; HidD_GetHidGuid(out guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new IOException("无法枚举 HID 设备");
        try {
            for (uint i=0;;i++) {
                var data = new Interface { Size=Marshal.SizeOf(typeof(Interface)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                uint needed;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out needed, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    // Only the V98 Pro V2/S99 protocol receiver; never send commands to unrelated peripherals.
                    if (path.IndexOf("vid_320f&pid_5088", StringComparison.OrdinalIgnoreCase)<0) continue;
                    using (var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                        if (handle.IsInvalid) continue;
                        IntPtr prep;
                        if (!HidD_GetPreparsedData(handle, out prep)) continue;
                        try {
                            Caps caps;
                            if (HidP_GetCaps(prep, out caps)!=0x110000) continue;
                            byte[] name = new byte[256]; HidD_GetProductString(handle,name,name.Length);
                            result.Add(new Device { Path=path, Name=Encoding.Unicode.GetString(name).TrimEnd('\0'), UsagePage=caps.Page, Usage=caps.Usage, Input=caps.Input, Output=caps.Output });
                        } finally { HidD_FreePreparsedData(prep); }
                    }
                } finally { Marshal.FreeHGlobal(detail); }
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
    public static Battery Parse(byte[] data, int length) {
        if (length<10 || data[0]!=4 || data[3]!=26 || data[8]>100 || data[9]>1) return null;
        return new Battery { Level=data[8], Charging=data[9]==1 };
    }
    public static async Task<Battery> Query(Device device, StringBuilder log) {
        if (device.UsagePage<0xFF00 || device.Output<8 || device.Input<10) return null;
        try {
            using (var handle = CreateFile(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero)) {
                if (handle.IsInvalid) { log.AppendLine("打开失败: " + Marshal.GetLastWin32Error()); return null; }
                using (var stream = new FileStream(handle, FileAccess.ReadWrite, device.Input, true)) {
                    HidD_FlushQueue(handle);
                    byte[] command=new byte[device.Output]; command[0]=4; command[3]=26; command[4]=6;
                    var write=stream.WriteAsync(command,0,command.Length);
                    if (await Task.WhenAny(write,Task.Delay(800))!=write) { Observe(write); log.AppendLine("写入超时"); return null; }
                    await write;
                    var deadline=DateTime.UtcNow.AddMilliseconds(1000);
                    for (int i=0;i<12 && DateTime.UtcNow<deadline;i++) {
                        byte[] response=new byte[device.Input];
                        var read=stream.ReadAsync(response,0,response.Length);
                        int remaining=Math.Max(1,(int)(deadline-DateTime.UtcNow).TotalMilliseconds);
                        if (await Task.WhenAny(read,Task.Delay(remaining))!=read) { Observe(read); log.AppendLine("响应超时"); return null; }
                        int count=await read;
                        if (count==0) break;
                        log.AppendLine("响应: " + BitConverter.ToString(response,0,count));
                        var battery=Parse(response,count); if (battery!=null) return battery;
                    }
                }
            }
        } catch (Exception e) { log.AppendLine(e.GetType().Name + ": " + e.Message); }
        return null;
    }
    static void Observe(Task task) { task.ContinueWith(t => { var ignored=t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
}
