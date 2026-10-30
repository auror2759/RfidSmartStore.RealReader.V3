using System;
using System.IO.Ports;
using System.Threading;
using ReaderB;

namespace RfidSmartStore.Local
{
    internal sealed class Kl9007uReader
    {
        private readonly object sync = new object();
        private readonly object connectSync = new object();
        private Thread worker;
        private Thread reconnectWorker;
        private volatile bool running;
        private volatile bool reconnectRunning;
        private volatile bool reconnectPaused;
        private byte address = 0xFF;
        private int portIndex = -1;
        private string portName = string.Empty;
        private int baudRate;
        private string lastEpc = string.Empty;
        private DateTime lastReadAt = DateTime.MinValue;
        private string presentEpc = string.Empty;
        private DateTime lastSeenAt = DateTime.MinValue;
        private long readSequence;
        private string error = string.Empty;

        public bool IsConnected { get { return running && portIndex >= 0; } }
        public string LastEpc { get { lock (sync) { return lastEpc; } } }
        public DateTime LastReadAt { get { lock (sync) { return lastReadAt; } } }
        public long ReadSequence { get { lock (sync) { return readSequence; } } }
        public string Error { get { lock (sync) { return error; } } }
        public string PortName { get { lock (sync) { return portName; } } }
        public int BaudRate { get { lock (sync) { return baudRate; } } }
        public bool AutoConnectEnabled { get { return reconnectRunning && !reconnectPaused; } }

        public void StartAutoReconnect()
        {
            if (reconnectRunning) return;
            reconnectPaused = false;
            reconnectRunning = true;
            reconnectWorker = new Thread(AutoReconnectLoop);
            reconnectWorker.IsBackground = true;
            reconnectWorker.Name = "KL9007U auto reconnect";
            reconnectWorker.Start();
        }

        public void Connect()
        {
            reconnectPaused = false;
            ConnectCore();
        }

        private void ConnectCore()
        {
            lock (connectSync)
            {
            if (IsConnected) return;
            int index = -1;
            int rc = 0x30;
            string[] ports = SerialPort.GetPortNames();
            byte[] baudCodes = new byte[] { 5, 4, 2, 1, 0 }; // 115200、57600、38400、19200、9600
            string connectedPort = string.Empty;
            int connectedBaud = 0;
            for (int p = 0; p < ports.Length && rc != 0; p++)
            {
                int portNumber;
                if (!int.TryParse(ports[p].Replace("COM", string.Empty), out portNumber)) continue;
                for (int b = 0; b < baudCodes.Length; b++)
                {
                    address = 0xFF;
                    index = -1;
                    rc = StaticClassReaderB.OpenComPort(portNumber, ref address, baudCodes[b], ref index);
                    if (rc == 0)
                    {
                        connectedPort = ports[p];
                        connectedBaud = BaudCodeToRate(baudCodes[b]);
                        break;
                    }
                    if (index >= 0) { try { StaticClassReaderB.CloseSpecComPort(index); } catch { } }
                }
            }
            if (rc != 0)
            {
                string message = "未能连接KL9007U；已扫描 " + (ports.Length == 0 ? "无可用串口" : string.Join(",", ports)) + "，SDK返回码：0x" + rc.ToString("X2");
                lock (sync) { error = message; portName = string.Empty; baudRate = 0; }
                throw new InvalidOperationException(message);
            }
            portIndex = index;
            lock (sync) { portName = connectedPort; baudRate = connectedBaud; error = string.Empty; }
            running = true;
            worker = new Thread(PollLoop);
            worker.IsBackground = true;
            worker.Name = "KL9007U inventory";
            worker.Start();
            }
        }

        public void Disconnect()
        {
            reconnectPaused = true;
            DisconnectCore();
        }

        private void DisconnectCore()
        {
            running = false;
            if (worker != null && worker.IsAlive) worker.Join(800);
            lock (connectSync)
            {
                if (portIndex >= 0) { try { StaticClassReaderB.CloseSpecComPort(portIndex); } catch { } }
                portIndex = -1;
                lock (sync) { portName = string.Empty; baudRate = 0; }
            }
        }

        public void Shutdown()
        {
            reconnectRunning = false;
            reconnectPaused = true;
            DisconnectCore();
        }

        private void AutoReconnectLoop()
        {
            while (reconnectRunning)
            {
                if (!reconnectPaused && !IsConnected)
                {
                    try { ConnectCore(); }
                    catch { }
                }
                Thread.Sleep(2000);
            }
        }

        public void Simulate(string epc)
        {
            AcceptTagReading(epc);
        }

        public void WriteEpc(string newEpc)
        {
            if (!IsConnected) throw new InvalidOperationException("请先连接读写器");
            if (string.IsNullOrEmpty(newEpc) || newEpc.Length % 4 != 0) throw new InvalidOperationException("EPC必须是4的倍数位十六进制字符");
            byte[] epc = HexToBytes(newEpc);
            byte[] password = new byte[] { 0, 0, 0, 0 };
            int tagError = 0;
            int rc = StaticClassReaderB.WriteEPC_G2(ref address, password, epc, Convert.ToByte(epc.Length), ref tagError, portIndex);
            if (rc != 0) throw new InvalidOperationException("写卡失败，SDK返回码：0x" + rc.ToString("X2") + "，标签错误：0x" + tagError.ToString("X2"));
            Thread.Sleep(180);
            Simulate(newEpc);
        }

        private void PollLoop()
        {
            int consecutiveFailures = 0;
            while (running)
            {
                try
                {
                    string currentPort = PortName;
                    string[] availablePorts = SerialPort.GetPortNames();
                    if (!string.IsNullOrEmpty(currentPort) && Array.FindIndex(availablePorts, delegate(string p) { return string.Equals(p, currentPort, StringComparison.OrdinalIgnoreCase); }) < 0)
                    {
                        lock (sync) { error = currentPort + "已断开，正在自动重连"; }
                        running = false;
                        break;
                    }
                    byte[] buffer = new byte[5000];
                    int total = 0;
                    int count = 0;
                    int rc = StaticClassReaderB.Inventory_G2(ref address, 0, 0, 0, buffer, ref total, ref count, portIndex);
                    bool validResult = rc == 0 || rc == 1 || rc == 2 || rc == 3 || rc == 4 || rc == 0xFB;
                    if (!validResult)
                    {
                        consecutiveFailures++;
                        lock (sync) { error = "读写器通信异常，SDK返回码：0x" + rc.ToString("X2") + (consecutiveFailures >= 3 ? "，正在自动重连" : ""); }
                        if (consecutiveFailures >= 3) { running = false; break; }
                        Thread.Sleep(180);
                        continue;
                    }
                    consecutiveFailures = 0;
                    lock (sync) { error = string.Empty; }
                    if ((rc == 1 || rc == 2 || rc == 3 || rc == 4 || rc == 0xFB) && count > 0 && total > 1)
                    {
                        int length = buffer[0];
                        if (length > 0 && length + 1 <= total)
                        {
                            byte[] epc = new byte[length];
                            Buffer.BlockCopy(buffer, 1, epc, 0, length);
                            string text = BytesToHex(epc);
                            AcceptTagReading(text);
                        }
                        else
                        {
                            lock (sync) { error = "检测到标签，但没有读取到有效EPC；请重新放卡或检查标签"; }
                        }
                    }
                    else MarkNoTagPresent();
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    lock (sync) { error = "读卡通信异常：" + ex.Message + (consecutiveFailures >= 3 ? "；正在自动重连" : ""); }
                    if (consecutiveFailures >= 3) { running = false; break; }
                }
                Thread.Sleep(180);
            }
            lock (connectSync)
            {
                if (portIndex >= 0) { try { StaticClassReaderB.CloseSpecComPort(portIndex); } catch { } }
                portIndex = -1;
                lock (sync) { portName = string.Empty; baudRate = 0; }
            }
        }

        private bool AcceptTagReading(string epc)
        {
            if (string.IsNullOrWhiteSpace(epc)) return false;
            DateTime now = DateTime.Now;
            lock (sync)
            {
                bool continuouslyPresent = string.Equals(epc, presentEpc, StringComparison.OrdinalIgnoreCase) && (now - lastSeenAt).TotalSeconds < 1.2;
                bool insideDuplicateWindow = string.Equals(epc, lastEpc, StringComparison.OrdinalIgnoreCase) && (now - lastReadAt).TotalSeconds < 5;
                presentEpc = epc;
                lastSeenAt = now;
                error = string.Empty;
                if (continuouslyPresent || insideDuplicateWindow) return false;
                lastEpc = epc;
                lastReadAt = now;
                readSequence++;
                return true;
            }
        }

        private void MarkNoTagPresent()
        {
            lock (sync)
            {
                if (lastSeenAt != DateTime.MinValue && (DateTime.Now - lastSeenAt).TotalSeconds >= 1.2) presentEpc = string.Empty;
            }
        }

        private static int BaudCodeToRate(byte code)
        {
            if (code == 0) return 9600;
            if (code == 1) return 19200;
            if (code == 2) return 38400;
            if (code == 4) return 57600;
            if (code == 5) return 115200;
            return 0;
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return result;
        }
        private static string BytesToHex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty); }
    }
}
