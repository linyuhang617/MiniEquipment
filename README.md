# Mini 設備控制器

C# WinForms 主控程式透過 TCP 控制模擬機台（個人練習專案）。

作者：linyuhang617

## 專案結構
- `src/Equipment.Simulator`：模擬機台（TCP Server，port 5000）
- `src/Controller.Core`：通訊與核心邏輯，不依賴 WinForms
- `src/Controller.WinForms`：操作畫面
- `tests/Controller.Core.Tests`：單元測試（xUnit）

## 執行
```
dotnet run --project src/Equipment.Simulator
dotnet run --project src/Controller.WinForms
```
在畫面按「連線」，狀態列顯示「已連線」。

## 測試
```
dotnet test
```

## 註解規範
- 每個檔案開頭有檔頭註解：檔案、專案、功能、`@author`、`@since`、`@version`
- 類別、屬性、函式使用 C# XML 文件註解（`/// <summary>`、`<param>`、`<returns>`、`<exception>`），Visual Studio 會在 IntelliSense 顯示

## 進度
- [x] Slice 0 Walking Skeleton：連線／中斷、逾時、失敗不當機
