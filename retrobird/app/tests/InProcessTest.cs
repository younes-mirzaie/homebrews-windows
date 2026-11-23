// Runs the C# client checks against the helper loaded as a library in this same process
// (librbmail.so here, rbmail.dll on the phone), the way the app's InAppHelper does.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

static class InProcessTest
{
    [DllImport("rbmail", CallingConvention = CallingConvention.Cdecl)]
    static extern int RetrobirdMain(int argc,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv);

    // args: port token password datadir
    static int Main(string[] args)
    {
        string[] argv = { "rbmail", "--token", args[1], "--port", args[0], "--data", args[3], "--sync-minutes", "0" };
        var t = new Thread(() => RetrobirdMain(argv.Length, argv)) { IsBackground = true };
        t.Start();
        Thread.Sleep(500);
        var main = typeof(InProcessTest).Assembly.GetType("ClientTest").GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic);
        int code = (int)main.Invoke(null, new object[] { new[] { args[0], args[1], args[2] } });
        // stop the helper (RetrobirdMain returns), then exit: its native threads would keep Mono waiting
        try
        {
            using (var c = new System.Net.Sockets.TcpClient("127.0.0.1", int.Parse(args[0])))
            {
                var w = new System.IO.StreamWriter(c.GetStream());
                w.Write("AUTH\t" + args[1] + "\nQUIT\n");
                w.Flush();
                Thread.Sleep(300);
            }
        }
        catch (Exception) { }
        Console.WriteLine(t.Join(5000) ? "helper stopped" : "helper still running at exit");
        Environment.Exit(code);
        return code;
    }
}
