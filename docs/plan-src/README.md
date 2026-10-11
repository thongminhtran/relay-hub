# RelayHub plan — source

The project plan docx is generated, not hand-edited.

- `plan.md` — the content (markdown subset: `#`/`##` headings, `-` bullets, `1.` numbered, `| tables |`, `:::note` / `:::tip` / `:::important` / `:::code` blocks, `![caption](image)`, `[[TOC]]`, `[[PAGEBREAK]]`, `<!-- widths: 20,80 -->` before a table).
- `build.js` — turns `plan.md` into a .docx with the `docx` npm package.
- `architecture.html` — the architecture diagram as inline SVG (1584 x 940).

## Rebuild

```powershell
cd projects\relay-hub\docs\plan-src
npm install            # first time only
node build.js ..\RelayHub-Project-Plan-v2.docx
```

Then open the docx in Word and accept "update fields" so the table of contents fills in (or run the Word COM snippet below).

```powershell
$word = New-Object -ComObject Word.Application
$doc = $word.Documents.Open("C:\Users\Thong Tran\projects\relay-hub\docs\RelayHub-Project-Plan-v2.docx")
$doc.Fields.Update() | Out-Null
foreach ($t in $doc.TablesOfContents) { $t.Update() | Out-Null }
$doc.Save(); $doc.Close(0); $word.Quit()
```

## Diagram

Edit `architecture.html`, then screenshot it with headless Chrome and copy the PNG over `../architecture.png`:

```powershell
& "C:\Program Files\Google\Chrome\Application\chrome.exe" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1.5 --window-size=1584,940 --user-data-dir="$env:TEMP\chrome-shot" --screenshot="$env:TEMP\architecture.png" "file:///C:/Users/Thong%20Tran/projects/relay-hub/docs/plan-src/architecture.html"
Copy-Item "$env:TEMP\architecture.png" ..\architecture.png
```
