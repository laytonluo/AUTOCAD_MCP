---
name: dwg-to-revit-columns
description: 依 Revit 平面視圖中的 DWG（匯入或連結）建立結構柱。使用者提供「視圖 ElementId, ImportInstance ElementId」（例如 347452,351751）時使用。流程依 5.1.3 操作文件：Revit MCP 掃描視圖與 DWG、AutoCAD MCP 讀取柱編號與尺寸文字，以 Ask User Question 逐題確認 Q1～Q9，預覽後經使用者確認才建立柱。Use when the user asks to model structural columns from a DWG in Revit, or invokes /dwg-to-revit-columns.
argument-hint: "<視圖ElementId>,<ImportInstanceElementId>  例：347452,351751"
---

# DWG → Revit 結構柱（5.1.3 MCP 流程）

本技能把 5.1.3《透過 MCP 依 DWG 平面建立 Revit 結構柱》的流程變成可呼叫的操作。
使用者輸入：`$ARGUMENTS`（第一個數字 = 視圖 ElementId，第二個 = ImportInstance ElementId）。

## 不可違反的規則

1. **分析與建立分離。** Q9 回答「建立」之前，不得開啟 Transaction、不得建立元素、不得寫入參數。
2. **一次只問一題。** 每個 Q 用一次 Ask User Question 工具，只放一個問題。等回答後再進下一步。
3. **選項必須來自實際掃描。** DWG、圖層、族群類型、參數名稱都要是掃描到的結果。COL、COLMN、COL_NAME、COL_S 只用來排序推薦，不存在就不列。
4. **例外要明列。** 未封閉、異形、無編號、無尺寸、無類型、資料矛盾，全部列在預覽與最終報告中，不得算成功。
5. **C60x80 與 C80x60 是不同類型。** 不自動互換寬高。
6. 每完成一個 Q 就更新 Decision Log（格式見文末），在步驟 02、05、08、10 顯示完整表格。

## 錄影友善的輸出格式

- 每一步開頭用一行標題：`### 步驟 03／10　選擇柱線圖層`。
- 工具呼叫前用一句話說明要做什麼；呼叫後用 3～6 行摘要結果，不要貼原始 JSON。
- Ask User Question 的推薦選項放第一個，標籤後加 `(Recommended)`；每個選項的 description 寫出數量或後果。
- 每題最多 4 個選項；「手動輸入」「貼上資料」交給工具自動提供的 Other 欄位，不要另做選項。

## 需要的工具

- **Revit MCP**：需能讀取元素，最好能執行 C# 程式碼（例如 `send_code_to_revit`、`execute_revit_code` 一類工具）。開始前先列出可用的 Revit 工具；若有現成的「取得元素」「建立柱」工具可直接用，沒有就用 `scripts/` 內的 C# 範本。
- **AutoCAD MCP**：同一張 DWG 需在 AutoCAD 開啟。Revit 的 ImportInstance 幾何**不含文字**，所以柱編號與尺寸文字必須從 AutoCAD 讀取。
  - 優先用 AutoCAD MCP 的圖層列表與圖元查詢工具取得 TEXT/MTEXT 的內容與插入點。
  - 若 MCP 只能執行 AutoLISP：載入 `scripts/coldump.lsp`，把 `*coldump-layers*` 改成要讀的圖層後執行 `COLDUMP`，再讀取輸出的 CSV。
- 若缺少 Revit MCP，停止並說明需要什麼；若缺少 AutoCAD MCP，可請使用者上傳 COLDUMP 匯出的 CSV 代替。

C# 範本說明：
- 範本以 `public static string Execute(Document doc)` 寫成，回傳 JSON 字串。依 Revit MCP 的程式碼格式調整外殼（例如有的工具直接提供 `document` 變數、要求 `return` 一個物件）。
- Revit 2024 以後用 `new ElementId(123L)`；2023 以前改成 `new ElementId(123)`。
- 每次執行前只改範本最上方 `// ===== 輸入 =====` 區塊。

## 流程

### 步驟 01　確認作用中視圖
1. 解析 `$ARGUMENTS`：允許「347452,351751」「347452 351751」等格式。缺值時用 Ask User Question 請使用者輸入（選項放「從目前作用中視圖讀取」，其餘交給 Other）。
2. 以 `scripts/revit_scan.cs`（填入兩個 ID）讀取：視圖名稱、ViewType、GenLevel、視圖中所有 ImportInstance。若兩個 ID 的類型對調（第一個是 ImportInstance），自動對調並說明。
3. 視圖不是 FloorPlan、EngineeringPlan（結構平面）或沒有 GenLevel → 回報「非平面視圖」並停止。
4. 摘要：視圖名稱、類型、樓層與高程。

### 步驟 02　DWG 來源（Q1）
- Ask User Question「請選擇目前視圖中的 DWG 來源」。
- 選項：使用者指定的 ImportInstance 放第一個 `(Recommended)`，label 為「名稱 #ElementId」，description 寫「連結/匯入、圖層數、曲線數」；其餘最多 2 個為視圖中掃描到的其他 ImportInstance；手動輸入交給 Other。
- 指定的 ID 不在此視圖中 → 在 description 註明，不要默默替換。
- 記錄 ImportInstanceId。顯示 Decision Log。

### 步驟 03　柱線圖層（Q2，必填）
- 從 `revit_scan.cs` 結果取得該 DWG 的圖層與各類曲線數。
- 推薦排序：名稱含 COL、COLUMN、COLMN，且有 Line/PolyLine/Arc 的圖層優先；description 寫曲線數。
- Ask User Question「請選擇柱輪廓圖層」。

### 步驟 04　柱編號圖層（Q3，選填）
- 用 AutoCAD MCP 讀取圖層列表與每層的 TEXT/MTEXT 數量與內容樣本。先確認 AutoCAD 開啟的檔名與 ImportInstance 名稱一致；不一致時先詢問使用者。
- 推薦：名稱含 COL_NAME、COLMN_N、AN-COL，或內容符合柱號格式（字母+數字，例如 C1、C12）的圖層。description 寫「TEXT 筆數、內容範例」。
- 必須有一個選項「不使用柱編號」，description 說明「無法寫入柱編號，也無法使用對照表」。
- Ask User Question「請選擇柱編號或柱名稱圖層」。
- 選定後取得該圖層全部文字：內容、插入點（DWG 座標）、Handle。

### 步驟 05　柱尺寸來源（Q4）
- Ask User Question「請選擇柱尺寸來源」，三個選項：
  1. 底圖柱線 Curve：依封閉輪廓量測。
  2. Curve + 柱尺寸標籤：再選尺寸文字圖層。
  3. Curve + 柱編號尺寸對照表：貼上「柱編號,尺寸」。
- Q3 選「不使用柱編號」時，選項 3 的 description 註明「需要柱編號，無法使用」。
- 顯示 Decision Log。

### 步驟 06　分支
- **06A（Curve）**：不需再問。
- **06B（尺寸標籤，Q5）**：Ask User Question「請選擇柱尺寸標籤圖層」。推薦 COL_S、COLMN_S、A5-COL 或內容符合 `60x80`、`(90x110)`、`R70`、`Ø70` 格式的圖層。選定後用 AutoCAD MCP 取得全部文字與插入點。
- **06C（對照表，Q6）**：Ask User Question「請貼上柱編號與尺寸對照資料（每列：柱編號,尺寸）」。
  - 選項 1：「由柱編號圖層 + 尺寸文字圖層產生」（僅在 AutoCAD 有尺寸文字圖層時提供），選項 2：「返回 Q4 改選尺寸來源」；貼上資料交給 Other。
  - 解析後先列表：成功、重複（同值只採用一次，不同值列為矛盾）、缺值、無法識別。確認後才進步驟 07。

### 步驟 07　資料對照（唯讀）
以 `scripts/revit_analyze.cs` 執行，填入 ImportInstanceId、柱線圖層、柱編號文字、尺寸來源與尺寸資料。程式會：
1. 讀取柱線圖層的 Line/PolyLine/Arc，以 1 mm 容差串接封閉環；矩形＝4 條直線且相鄰邊垂直，圓形＝封閉 Arc；其他列人工複核。
2. 自動判斷 DWG 單位（mm/cm/m/inch/ft），以柱編號文字落在柱旁的比例最高者為準，並回報判斷結果。
3. 柱編號：柱質心到文字插入點最近且唯一，最大距離 2500 mm。
4. 尺寸標籤：先配對到最近的柱編號文字（標籤常放在柱號正下方），沒有柱編號時才直接配對柱質心。
5. 尺寸正規化為 cm，四捨五入到 0.5 cm；與量測尺寸差超過 0.5 cm → 資料矛盾。
6. 族群類型依 `C{W}x{H}cm`、`C-R{D}cm` 規則比對專案中的 Structural Column FamilySymbol（名稱中 W、H 的順序必須一致）。
7. 以視圖中最近的兩向軸線組成 PositionKey（例如 A1）；沒有軸線時用 X1-Y1 格網編號。PositionKey 不取代柱編號。

### 步驟 08　預覽與討論
- 顯示預覽表（欄位：建立、位置識別、柱編號、幾何類型、圖面尺寸、帶入尺寸、對應族群類型、樓層、狀態、訊息）。超過 30 列時先列例外，再列已配對的前 20 列並註明總數。
- 另外分組列出：無編號、無類型、資料矛盾、排除、未使用的標籤。
- 摘要：輪廓數、有效柱數、尺寸來源、各類型數量。顯示 Decision Log。

### 步驟 09　參數寫入（Q7、Q8）
- **Q7** Ask User Question「是否將柱編號寫入 Revit 參數」：是／否。Q3 為「不使用」時直接記為否並說明。
- **Q8**（Q7 = 是）：以 `scripts/revit_params.cs` 掃描結構柱可寫入的字串實例參數。推薦「標記 (Mark)」，其次「註解 (Comments)」與專案共用參數。Ask User Question「請選擇柱編號寫入欄位」。

### 步驟 10　最終確認（Q9）與建立
1. 顯示最終摘要：準備建立數（依類型分列）、人工複核、無類型略過、排除、將寫入的參數。
2. **Q9** Ask User Question「是否依預覽結果建立結構柱？」：
   - `建立`：description 寫「開啟 Transaction，建立 N 根結構柱並寫入〈參數〉；其餘 M 根不建立」
   - `返回修正`：回到對應的 Q 重新選擇
   - `取消`：不修改模型，保留 Decision Log
3. 只有選「建立」時，以 `scripts/revit_commit.cs` 建立：只帶狀態為「已配對」的列，使用柱中心、視圖 GenLevel、FamilySymbol 與 DWG 旋轉角。
4. 回報逐列結果（成功、略過、失敗與原因）與總結，並顯示最終 Decision Log。

## Decision Log 格式

| 決策項目 | 使用者選擇 | 來源／識別碼 | 狀態 |
|---|---|---|---|
| 作用中視圖 | 名稱 | ViewId / GenLevel | 已確認 |
| Q1 DWG 來源 | 名稱 | ImportInstanceId | TBD |
| Q2 柱線圖層 | | Layer Name | TBD |
| Q3 柱編號圖層 | | Layer Name / 不使用 | TBD |
| Q4 尺寸來源 | | Curve / Size Tag / Mapping | TBD |
| Q5／Q6 尺寸圖層或對照表 | | Layer / pasted data | 條件式 |
| Q7 寫入柱編號 | | 是 / 否 | TBD |
| Q8 寫入參數 | | Parameter Name | 條件式 |
| Q9 最終建立 | | 建立 / 修正 / 取消 | TBD |

## 驗收（最終報告前自我檢查）
- 建立樓層 = 視圖 GenLevel。
- DWG、圖層、類型、參數名稱都來自掃描或使用者輸入。
- 預覽筆數 = 有效封閉輪廓數；同一標籤沒有分配給兩根柱。
- 無類型、資料矛盾、排除不在成功數中。
- Q9 回答「建立」之前沒有任何 Transaction。
