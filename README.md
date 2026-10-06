# Mini 設備控制器

C# WinForms 主控程式透過 TCP 控制模擬機台（個人練習專案）。

作者：linyuhang617

## 專案結構
- `src/Equipment.Simulator`：模擬機台（TCP Server，port 5000）
- `src/Controller.Core`：通訊與核心邏輯，不依賴 WinForms
  - `Connection/`：`IDeviceConnection` 傳輸層抽象、`TcpDeviceConnection` TCP 實作、
    `ConnectionSupervisor` 心跳與自動重連
  - `Protocol/`：`LineFramer` 封包切割、`DeviceClient` 請求／回應與逾時
- `src/Controller.WinForms`：操作畫面
- `tests/Controller.Core.Tests`：單元測試（xUnit），`Fakes/` 為測試用假連線

## 通訊協定
一行一筆訊息，以 `\n` 結尾，UTF-8 編碼。

| 主控送出 | 機台回覆 | 說明 |
| --- | --- | --- |
| `GET STATUS` | `OK IDLE` | 機台故意分兩段送（`OK ` 與 `IDLE\n`），用來驗證封包切割 |
| `PING` | `OK PONG` | 心跳，主控每 5 秒自動送一次 |
| `SLEEP` | （不回應） | 用來驗證 3 秒指令逾時 |
| `HANG` | `OK HANG` | 之後這條連線完全不回應，模擬設備當機，用來驗證心跳偵測 |
| 其他 | `ERR UNKNOWN_COMMAND` | |

## 斷線重連機制
- **心跳**：每 5 秒送 `PING`，2 秒內沒回應算漏一次，連續 2 次就判定斷線。
  用來偵測「連線還在但設備沒反應」，例如設備當機或網路線被拔掉。
- **斷線偵測**：設備主動關閉連線時立即察覺，不用等心跳。
- **自動重連**：間隔 1、2、4、8、16 秒遞增，最多 30 秒（指數退避），避免設備還沒起來時一直狂連。
- 第一次連線失敗不自動重試，直接顯示錯誤，讓使用者檢查 IP 與 Port。

## 執行
```
dotnet run --project src/Equipment.Simulator
dotnet run --project src/Controller.WinForms
```
在畫面按「連線」，再選指令按「送出」。

## 測試
```
dotnet test
```

## 註解規範
- 每個檔案開頭有檔頭註解：檔案、專案、功能、`@author`、`@since`、`@version`
- 類別、屬性、函式使用 C# XML 文件註解（`/// <summary>`、`<param>`、`<returns>`、`<exception>`），Visual Studio 會在 IntelliSense 顯示

## 進度
- [x] Slice 0 Walking Skeleton：連線／中斷、逾時、失敗不當機
- [x] Slice 1 送指令、收回應：封包切割（半包、黏包）、3 秒逾時、逾時後的遲到回應不會錯配
- [x] Slice 2 斷線重連：5 秒心跳、連續 2 次沒回應判定斷線、1→2→4…→30 秒指數退避自動重連
- [ ] Slice 3 機台狀態機
