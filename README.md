# Mini 設備控制器

C# WinForms 主控程式透過 TCP 控制模擬機台（個人練習專案）。

作者：linyuhang617

## 專案結構
- `src/Equipment.Simulator`：模擬機台（TCP Server，port 5000）
- `src/Controller.Core`：通訊與核心邏輯，不依賴 WinForms
  - `Connection/`：`IDeviceConnection` 傳輸層抽象、`TcpDeviceConnection` TCP 實作
  - `Protocol/`：`LineFramer` 封包切割、`DeviceClient` 請求／回應與逾時
- `src/Controller.WinForms`：操作畫面
- `tests/Controller.Core.Tests`：單元測試（xUnit）

## 通訊協定
一行一筆訊息，以 `\n` 結尾，UTF-8 編碼。

| 主控送出 | 機台回覆 | 說明 |
| --- | --- | --- |
| `GET STATUS` | `OK IDLE` | 機台故意分兩段送（`OK ` 與 `IDLE\n`），用來驗證封包切割 |
| `SLEEP` | （不回應） | 用來驗證 3 秒逾時 |
| 其他 | `ERR UNKNOWN_COMMAND` | |

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
- [ ] Slice 2 斷線重連
