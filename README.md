# KL9007U RFID 真实读卡 V3

本程序把现有网页界面、本地 SQLite 数据库和恺乐 KL9007U 原厂 SDK 连接起来。

## 使用方法

1. 通过 USB 连接 KL9007U，确认设备管理器里出现可用 COM 端口。
2. 双击 `启动系统.cmd`，程序会打开 `http://127.0.0.1:8789/`。
3. C#后端每2秒自动扫描串口及原厂SDK支持的波特率，无需浏览器选择COM口。
4. 刷未开卡标签会进入开卡流程；刷已开卡标签会自动进入付款与充值界面。
5. 输入金额后选择“从余额付款”或“充值到会员卡”。

数据保存在 `bin\Release\data\rfid-store.db`。充值、消费和余额变化均写入不可变流水。

## 真实写卡

开卡时勾选“调用原厂 DLL 写入新的 EPC”，后端将调用 `WriteEPC_G2`。执行前请确保感应区内只有一张卡，并确认标签 EPC 区允许写入、访问密码为 `00000000`。

## 文件说明

- `Kl9007uReader.cs`：原厂 DLL 连接、盘存、EPC 写入。
- `StoreRepository.cs`：会员、开卡、充值、付款和流水事务。
- `SqliteDb.cs`：SQLite 原生接口封装。
- `StoreServer.cs`：本地网页和 API 服务。
- `web\index.html`：付款、充值和开卡界面。

项目按 x86 编译，以兼容原厂 `UHFReader18CSharp.dll` 和 `Basic.dll`。网页显示的COM端口和波特率来自C#后端真实连接状态，不使用浏览器串口模拟。

## 从源码构建

Git 仓库不会包含真实会员数据库、编译产物和厂商 DLL。编译前请将合法获得的以下文件放入 `lib` 目录：

- `UHFReader18CSharp.dll`
- `Basic.dll`
- `sqlite3.dll`

然后运行 `重新编译.cmd`。程序首次启动时会在 `bin\Release\data` 中自动创建本地数据库。
