using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Windows pushes HID interface arrivals/removals to this invisible top-level window.
internal sealed class DeviceNotifications : NativeWindow, IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct Filter {
        public int Size, Type, Reserved;
        public Guid ClassGuid;
        public short Name;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr RegisterDeviceNotification(IntPtr window, ref Filter filter, uint flags);
    [DllImport("user32.dll")] static extern bool UnregisterDeviceNotification(IntPtr notification);
    IntPtr registration;
    public event Action<bool> Changed;
    public DeviceNotifications() {
        CreateHandle(new CreateParams { Caption="V98 USB device notifications" });
        Guid guid; HidD_GetHidGuid(out guid);
        var filter=new Filter { Size=Marshal.SizeOf(typeof(Filter)), Type=5, ClassGuid=guid };
        registration=RegisterDeviceNotification(Handle,ref filter,0);
        if(registration==IntPtr.Zero) { int error=Marshal.GetLastWin32Error(); DestroyHandle(); throw new Win32Exception(error); }
    }
    internal static bool IsReceiver(string path) {
        return path!=null && path.IndexOf("vid_320f&pid_5088",StringComparison.OrdinalIgnoreCase)>=0;
    }
    protected override void WndProc(ref Message message) {
        long change=message.WParam.ToInt64();
        if(message.Msg==0x219 && (change==0x8000 || change==0x8004) && message.LParam!=IntPtr.Zero) {
            int size=Marshal.ReadInt32(message.LParam);
            if(size>=30 && Marshal.ReadInt32(message.LParam,4)==5) {
                string path=Marshal.PtrToStringUni(IntPtr.Add(message.LParam,28),(size-28)/2).TrimEnd('\0');
                if(IsReceiver(path)) { var handler=Changed; if(handler!=null) handler(change==0x8000); }
            }
        }
        base.WndProc(ref message);
    }
    public void Dispose() {
        if(registration!=IntPtr.Zero) { UnregisterDeviceNotification(registration); registration=IntPtr.Zero; }
        DestroyHandle();
    }
}
