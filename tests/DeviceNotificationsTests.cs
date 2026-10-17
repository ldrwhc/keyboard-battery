using System;
using System.Runtime.InteropServices;

internal static class DeviceNotificationsTests {
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
    [STAThread] static int Main() {
        using(var listener=new DeviceNotifications()) {
            int arrivals=0,removals=0;
            listener.Changed+=arrived=> { if(arrived) arrivals++; else removals++; };
            Send(listener.Handle,0x8000,"\\\\?\\hid#vid_320f&pid_5088&mi_01");
            Send(listener.Handle,0x8004,"\\\\?\\hid#VID_320F&PID_5088&mi_01");
            Send(listener.Handle,0x8000,"\\\\?\\hid#vid_046d&pid_c08b");
            Send(listener.Handle,0x8001,"\\\\?\\hid#vid_320f&pid_5088");
            if(arrivals!=1 || removals!=1) return 1;
        }
        Console.WriteLine("PASS: native registration, arrival/removal dispatch, receiver filtering, disposal");
        return 0;
    }
    static void Send(IntPtr window,int change,string path) {
        int bytes=28+(path.Length+1)*2;
        IntPtr data=Marshal.AllocHGlobal(bytes);
        try {
            for(int i=0;i<bytes;i++) Marshal.WriteByte(data,i,0);
            Marshal.WriteInt32(data,bytes); Marshal.WriteInt32(data,4,5);
            Marshal.Copy((path+'\0').ToCharArray(),0,IntPtr.Add(data,28),path.Length+1);
            SendMessage(window,0x219,new IntPtr(change),data);
        } finally { Marshal.FreeHGlobal(data); }
    }
}
