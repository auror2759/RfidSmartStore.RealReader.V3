using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RfidSmartStore.Local
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            try { Run(args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("启动失败：" + ex.GetType().FullName);
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                Environment.ExitCode = 1;
            }
        }

        private static void Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDir, "data");
            Directory.CreateDirectory(dataDir);
            StoreRepository repository = new StoreRepository(Path.Combine(dataDir, "rfid-store.db"));
            Kl9007uReader reader = new Kl9007uReader();
            StoreServer server = new StoreServer("http://127.0.0.1:8789/", baseDir, repository, reader);

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                server.Stop();
            };

            server.Start();
            reader.StartAutoReconnect();
            Console.WriteLine("社区便利店 RFID 真实读卡 V3 已启动：http://127.0.0.1:8789/");
            Console.WriteLine("读写器将自动扫描COM端口并连接KL9007U。");
            Console.WriteLine("数据文件：" + Path.Combine(dataDir, "rfid-store.db"));
            Console.WriteLine("按 Ctrl+C 停止服务。");
            bool noOpen = args != null && Array.IndexOf(args, "--no-open") >= 0;
            if (!noOpen) try { Process.Start("http://127.0.0.1:8789/"); } catch { }
            server.Wait();
        }
    }
}
