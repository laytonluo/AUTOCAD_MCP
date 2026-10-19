---
name: dwg-to-revit-columns
description: 依 Revit 平面視圖中的 DWG（匯入或連結）建立結構柱。使用者輸入 /dwg-to-revit-columns 或要求「依 DWG 柱線翻模結構柱」時使用。流程依 5.1.3 操作文件：Revit MCP 掃描視圖與 DWG、AutoCAD MCP 讀取柱編號與尺寸文字，以 Ask User Question 逐題確認 Q1～Q10，列表確認後才建立柱。Use when the user invokes /dwg-to-revit-columns or asks to model structural columns from a DWG in Revit.
---

# DWG → Revit 結構柱（5.1.3 MCP 流程）

## 語言

所有回覆、步驟標題、Ask User Question 的問題與選項、表格欄位與最終報告，一律使用**繁體中文**。
圖層名稱、檔名、ElementId、參數名稱等實際資料保持原樣。

## 不可違反的規則

1. **分析與建立分離。** Q10 回答「建立」之前，不得開啟 Transaction、不得建立元素、不得寫入參數。
2. **一次只問一題。** 每個 Q 用一次 Ask User Question 工具，只放一個問題，等回答後才進下一步。
3. **選項必須來自實際掃描。** 視圖、DWG、圖層、族群類型、參數名稱都要是掃描結果。COL、COLMN、COL_NAME、COL_S 只用來排序推薦，不存在就不列。
4. **例外要明列。** 未封閉、異形、無編號、無尺寸、無類型、資料矛盾，全部列出，不得算成功。
5. **C60x80 與 C80x60 是不同類型。** 不自動互換寬高。
6. 每完成一個 Q 就更新 Decision Log（格式見文末），在 Q2、Q5、Q10 前與最終報告顯示完整表格。

## 錄影友善的輸出格式

- 每一步開頭用一行標題，例如 `### Q3／10　柱線圖層`。
- 工具呼叫前用一句話說明要做什麼；呼叫後用 3～6 行中文摘要，不貼原始 JSON。
- 推薦選項放第一個，標籤後加「（建議）」；每個選項的說明寫出數量或後果。
- 每題 2～4 個選項；「自行輸入」「貼上資料」交給工具自動提供的「其他（Other）」欄位，不另做選項。

## 需要的工具

- **Revit MCP**：需能執行 C# 程式碼（例如 `send_code_to_revit`、`execute_revit_code` 一類工具）。開始前先列出可用的 Revit 工具；有現成的「取得元素」「建立柱」工具可直接用，沒有就用 `scripts/` 內的 C# 範本。
- **AutoCAD MCP**：同一張 DWG 需在 AutoCAD 開啟。Revit 的 DWG 幾何**不含文字**，柱編號與尺寸文字必須從 AutoCAD 讀取。
  - 優先用 AutoCAD MCP 的圖層列表與圖元查詢工具，取得 TEXT/MTEXT 的內容與插入點。
  - 若只能執行 AutoLISP：載入 `scripts/coldump.lsp`，把 `*coldump-layers*` 改成要讀的圖層後執行 `COLDUMP`，再讀取輸出的 CSV。
- 缺少 Revit MCP → 停止並說明需求；缺少 AutoCAD MCP → 請使用者上傳 COLDUMP 匯出的 CSV 代替。

C# 範本說明：
- 範本以 `public static string Execute(Document doc)` 撰寫並回傳 JSON 字串，依 Revit MCP 的程式碼格式調整外殼。
- Revit 2024 以後用 `new ElementId(123L)`；2023 以前改成 `new ElementId((int)123)`、`.Value` 改成 `.IntegerValue`。
- 每次執行只改範本最上方 `// ===== 輸入 =====` 區塊。

## 流程

### Q1／10　作業視圖
1. 以 `scripts/revit_scan.cs`（`VIEW_KEY = ""`）讀取目前作用中視圖：名稱、ElementId、類型、樓層（GenLevel）。
2. Ask User Question「請確認作業視圖」：
   - 選項 1：「使用目前視圖：〈視圖名稱〉#〈ID〉（建議）」，說明寫「類型、樓層、視圖中 DWG 數量」。
   - 選項 2～3：專案中其他含 DWG 的平面視圖（有才列）。
   - 使用者可在「其他」輸入視圖名稱或 ElementId。
3. 輸入名稱或 ID 時，以 `VIEW_KEY = "名稱或ID"` 重新執行 `revit_scan.cs`。找不到或不是平面視圖（FloorPlan、EngineeringPlan，且有 GenLevel）→ 說明原因並重問 Q1。

### Q2／10　DWG 圖面
1. 由 `revit_scan.cs` 結果取得該視圖中所有 ImportInstance。視圖中沒有 DWG → 回報並回到 Q1。
2. Ask User Question「請確認要使用的 DWG 圖面」：
   - 最多 3 個選項，label 為「DWG 名稱 #ElementId」，說明寫「連結／匯入、圖層數、柱線候選圖層」。只有一個時放第一個並標「（建議）」，再加一個「返回 Q1 改選視圖」。
   - 使用者可在「其他」輸入 DWG 名稱或 ElementId。
3. 以 `IMPORT_KEY` 填入選定項目再執行一次 `revit_scan.cs`，取得該 DWG 的圖層與曲線數。顯示 Decision Log。

### Q3／10　柱線圖層（必填）
- 推薦：名稱含 COL、COLUMN、COLMN 且有 Line／PolyLine／Arc 的圖層；說明寫曲線數。
- Ask User Question「請選擇柱輪廓圖層」。

### Q4／10　柱編號圖層（選填）
- 用 AutoCAD MCP 讀取圖層列表與每層的 TEXT/MTEXT 數量與內容範例。先確認 AutoCAD 開啟的檔名與 Q2 的 DWG 一致；不一致時先詢問。
- 推薦：名稱含 COL_NAME、COLMN_N、AN-COL，或內容符合柱號格式（字母＋數字，如 C1、C12）的圖層。說明寫「文字筆數、內容範例」。
- 必須提供「不使用柱編號」，說明「無法寫入柱編號，也無法使用對照表」。
- Ask User Question「請選擇柱編號圖層」。選定後取得全部文字：內容、插入點（DWG 座標）、Handle。

### Q5／10　柱尺寸來源
- Ask User Question「請選擇柱尺寸來源」：
  1. 底圖柱線：依封閉輪廓量測。
  2. 柱線＋尺寸標籤：接著選尺寸文字圖層（Q6）。
  3. 柱線＋柱編號尺寸對照表：接著貼上對照資料（Q7）。
- Q4 選「不使用柱編號」時，選項 3 的說明註明「需要柱編號，無法使用」。
- 顯示 Decision Log。

### Q6／10　尺寸標籤圖層（僅 Q5 = 2）
- 推薦 COL_S、COLMN_S、A5-COL，或內容符合 `60x80`、`(90x110)`、`R70`、`Ø70` 格式的圖層。
- Ask User Question「請選擇柱尺寸標籤圖層」。選定後用 AutoCAD MCP 取得全部文字與插入點。

### Q7／10　尺寸對照表（僅 Q5 = 3）
- Ask User Question「請貼上柱編號與尺寸對照資料（每列：柱編號,尺寸）」。
  - 選項 1：「由柱編號圖層＋尺寸文字圖層產生」（AutoCAD 有尺寸文字圖層時才提供）；選項 2：「返回 Q5 改選尺寸來源」；貼上資料交給「其他」。
- 先列出解析結果：成功、重複（同值只採用一次，不同值列為矛盾）、缺值、無法識別，再進入分析。

### 資料對照（唯讀，不需詢問）
以 `scripts/revit_analyze.cs` 執行，填入 Q1～Q7 的結果。程式會：
1. 讀取柱線圖層的 Line／PolyLine／Arc，以 1 mm 容差串接封閉環；矩形＝4 條直線且相鄰邊垂直，圓形＝封閉 Arc，其他列人工複核。
2. 自動判斷 DWG 單位（mm／cm／m／inch／ft）並回報。
3. 柱編號：柱質心到文字插入點最近且唯一，最大距離 2500 mm。
4. 尺寸標籤：先配對到最近的柱編號文字，沒有柱編號時才直接配對柱質心。
5. 尺寸以 cm 正規化、四捨五入到 0.5 cm；與量測差超過 0.5 cm → 資料矛盾。
6. 族群類型依 `C{W}x{H}cm`、`C-R{D}cm` 規則比對專案中的結構柱類型（寬高順序必須一致）。
7. 結果依柱編號自然排序（C1、C2…C10），無編號的排最後。

### Q8／10　是否寫入柱編號
- Ask User Question「是否將柱編號寫入 Revit 參數」：是／否。Q4 為「不使用」時直接記為否並說明。

### Q9／10　寫入欄位（僅 Q8 = 是）
- 以 `scripts/revit_params.cs` 掃描結構柱可寫入的字串實例參數。推薦「標記」，其次「註解」與專案參數。
- Ask User Question「請選擇柱編號寫入欄位」。

### Q10／10　最終確認與建立
1. **先列出確認表**，欄位順序固定如下，依柱編號 C1、C2、C3… 排序：

   | 柱子編號 | 圖面幾何尺寸 | 圖面標籤尺寸 | 使用的柱類型 |
   |---|---|---|---|
   | C1 | 110x90 | 110x90 | C110x90cm |
   | C9 | 90x90 | 100x90 | 不建立（資料矛盾） |

   - 圖面幾何尺寸＝`measured`；圖面標籤尺寸＝`source`（尺寸來源為底圖柱線時填「—」）；使用的柱類型＝`type`，不建立的列寫「不建立（原因）」。
   - 超過 40 列時，先列全部不建立的列，再列建立的列並註明總數。
2. 表格下方列出摘要：將建立 N 根（依類型分列）、不建立 M 根（依原因分列）、未使用的標籤、寫入參數。
3. Ask User Question「是否依確認表建立結構柱？」：
   - 「建立」：說明寫「建立 N 根結構柱並寫入〈參數〉；其餘 M 根不建立」
   - 「返回修正」：回到對應的 Q 重新選擇
   - 「取消」：不修改模型，保留 Decision Log
4. 只有選「建立」時，以 `scripts/revit_commit.cs` 建立：只帶 `commitRows`，使用柱中心、視圖樓層、柱類型與 DWG 旋轉角。
5. 回報逐根結果（成功、略過、失敗與原因）與總結，並顯示最終 Decision Log。

## Decision Log 格式

| 決策項目 | 使用者選擇 | 來源／識別碼 | 狀態 |
|---|---|---|---|
| Q1 作業視圖 | | ViewId／樓層 | 待確認 |
| Q2 DWG 圖面 | | ImportInstanceId | 待確認 |
| Q3 柱線圖層 | | 圖層名稱 | 待確認 |
| Q4 柱編號圖層 | | 圖層名稱／不使用 | 待確認 |
| Q5 尺寸來源 | | 柱線／尺寸標籤／對照表 | 待確認 |
| Q6／Q7 尺寸圖層或對照表 | | 圖層／貼上資料 | 條件式 |
| Q8 寫入柱編號 | | 是／否 | 待確認 |
| Q9 寫入欄位 | | 參數名稱 | 條件式 |
| Q10 最終確認 | | 建立／返回修正／取消 | 待確認 |

## 驗收（最終報告前自我檢查）
- 建立樓層＝Q1 視圖的樓層。
- 視圖、DWG、圖層、類型、參數名稱都來自掃描或使用者輸入。
- 確認表筆數＝有效封閉輪廓數；同一標籤沒有分配給兩根柱。
- 無類型、資料矛盾、排除不在成功數中。
- Q10 回答「建立」之前沒有任何 Transaction。
