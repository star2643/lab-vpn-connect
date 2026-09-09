# lab-vpn-connect

Windows SSH 的 VPN 連線小工具。讓指定的 SSH 連線經過已連線的 VPN，保留原本的目標 IP 和 port，不必設定跳板。

適用情境：VPN 伺服器與 SSH port 轉發共用同一個公網 IP。直接修改整個 IP 的路由可能干擾 VPN 本身；本工具只指定自己的 TCP socket 使用哪個 VPN 介面。

## 下載與安裝

從 [Releases](https://github.com/star2643/lab-vpn-connect/releases) 下載 Windows ZIP，完整解壓縮，執行 `Install.cmd`，輸入管理員提供的公網 IPv4 和自己的 SSH 帳號。預設 VPN 名稱為 `lab`、SSH 別名為 `work`、port 為 `10137`。

需要自訂時，在解壓縮的資料夾中執行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -HostName 203.0.113.10 -Port 10137 -UserName your_account -VpnName lab -Alias work
```

`203.0.113.10` 是文件範例，必須換成 lab 的實際位址。程式不內建任何 lab 位址、VPN 帳密或 SSH 帳密。

安裝程式會：

- 將單一 exe 複製到 `%LOCALAPPDATA%\Programs\lab-vpn-connect\`。
- 備份現有 `.ssh/config`，在開頭加入有標記的 SSH 設定；原本內容保留。
- 重複安裝同一個別名時更新該區塊，不重複新增。不修改系統 PATH、VPN、路由或防火牆。

安裝完成後，解壓縮的 ZIP、`Install.cmd`、`install.ps1` 都可以刪除。安裝目錄中的 exe 和 `.ssh/config` 必須保留。

## 平常怎麼使用

先連 VPN，再執行 `ssh work`，或在 VS Code Remote SSH 選擇 `work`。登入密碼仍在 SSH / VS Code 的原本提示中輸入。

設定的主要部分如下（安裝程式會自動填入自己的絕對路徑）：

```sshconfig
Host work
    HostName 203.0.113.10
    Port 10137
    User your_account
    ProxyCommand "C:/Users/YOUR_NAME/AppData/Local/Programs/lab-vpn-connect/lab-vpn-connect.exe" --interface "lab" --host %h --port %p
```

如需另一台 SSH 主機，新增另一個 `Host`、改 `Port` / `User`，沿用同一行 `ProxyCommand`。也可以重新執行安裝程式並給不同的 `-Alias`。

這是指定 SSH 連線的工具，不會攔截全電腦流量。直接執行 `ssh user@203.0.113.10 -p 10137` 而沒有符合的設定，並不會自動套用 `Host work`。

## 運作方式與限制

1. 使用 Windows 網路 API 讀取名稱符合的已連線 PPP/Tunnel 介面與當前 IPv4，不寫死使用者獲配的 IP。
2. 對新 TCP socket 同時設定 `IP_UNICAST_IF` 和本機來源 IP。這不更動路由表，也不更動外層 VPN 連線。
3. 雙向轉送原始位元組；標準輸出只承載 SSH 資料，診斷走標準錯誤輸出。SSH 的加密、主機金鑰檢查與身分驗證由 OpenSSH 處理。
4. 找不到 VPN、介面不符、VPN 位址改變或連線失敗時停止，不嘗試一般網路。中途 VPN 中斷後，需要重新連線。

需要 Windows 10/11、內建 .NET Framework 4.x，以及 OpenSSH Client（`ssh.exe`）。不需要 Python、另外安裝 .NET SDK、系統管理員權限或常駐服務。發布的 exe 未簽署。

目前只接受名稱明確且有單一 IPv4 的 PPP/Tunnel 介面；Windows 內建 PPTP VPN 已實測。其他 VPN 用戶端可能將介面呈現為 Ethernet，本版會拒絕，不宣稱支援所有 VPN 軟體。目標必須是數字 IPv4，暫不做 DNS 或 IPv6。

本工具只選擇連線路徑。VPN 到目標的路由、路由器的 NAT/port 轉發以及主機防火牆仍須由管理員設定；本工具不會使原本無法到達的服務自動可達。若要求只有 VPN 能存取服務，必須在伺服器或路由器限制來源，不能只依靠成員自願使用這個工具。

## 診斷

```powershell
& "$env:LOCALAPPDATA\Programs\lab-vpn-connect\lab-vpn-connect.exe" --interface lab --host 203.0.113.10 --port 10137 --check
```

成功時顯示實際 VPN 介面索引、來源 IP 和目標。此檢查只建立 TCP 連線，不驗證 SSH 帳密。`--timeout 10` 可指定連線逾時（1–120 秒）。`--verbose` 顯示傳輸區塊大小，供除錯使用，不輸出資料內容。

退出碼：`0` 成功；`2` 參數錯誤；`3` VPN 不可用；`4` 連線或轉送失敗。

## 原始碼與自行編譯

完整程式在 [`src/Program.cs`](src/Program.cs)，MIT 授權。沒有 NuGet 或其他第三方執行期依賴。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Installer.Tests.ps1
```

使用 Windows 內建 .NET Framework C# 編譯器產生 AnyCPU exe；結果在 `artifacts/`。一般成員只需要發布版 exe，無須編譯。`package.ps1` 產生 ZIP 和 SHA-256 清單。

測試包含無 VPN／介面停用／錯選 Ethernet 拒絕、VPN IP 改變、參數驗證、1 MiB 任意二進位資料傳輸與 TCP 半關閉，以及安裝程式對空白路徑、重複執行、既有設定和解除安裝的處理。CI 不會連線到任何 lab。

Windows OpenSSH 會傳入 overlapped pipe handles，因此標準輸入輸出使用相容的 `FileStream`；不可直接換成 .NET Framework 的 `Console.OpenStandardInput()`，否則 SSH 握手可能停住。

## 移除

重新下載原始碼或 ZIP，執行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1 -Alias work -RemoveExecutable
```

移除指定別名的管理區塊，保留原始設定和備份。如果其他設定仍引用 exe，會保留 exe。手動設定的使用者可自行移除對應 `ProxyCommand` 和安裝目錄內的 exe。

## 技術文件

- [Microsoft: IP_UNICAST_IF socket option](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options)
- [OpenSSH: ProxyCommand](https://man.openbsd.org/ssh_config.5#ProxyCommand)
