// RelayHub plan generator: plan.md -> .docx (docx-js)
// Usage: node build.js [output.docx]   (default ../RelayHub-Project-Plan.docx)
// Markdown subset: # / ## / ### headings, "- " bullets, "1. " numbered, | tables |,
// :::cover / :::note / :::tip / :::important / :::code blocks, ![caption](image.png),
// [[TOC]], [[PAGEBREAK]], <!-- widths: 25,75 --> before a table, <!-- noheader -->.
// Inline: **bold**, *italic*, `code`, [text](url), \* for a literal asterisk.
const fs = require('fs');
const path = require('path');
const {
  Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell, WidthType, ShadingType,
  BorderStyle, AlignmentType, HeadingLevel, LevelFormat, ImageRun, PageBreak, TableOfContents,
  Footer, PageNumber, TabStopType, ExternalHyperlink, VerticalAlign,
} = require('docx');

const C = {
  navy: '1F3A5F', blue: '0078D4', slate: '64748B', text: '1E293B', border: 'CBD5E1',
  headBorder: 'DCE9F7', zebra: 'F8FAFC', section: 'E8F1FB', code: 'F1F5F9',
  note: { fill: 'ECFDF5', bar: '059669', label: 'Note' },
  tip: { fill: 'EFF6FF', bar: '0078D4', label: 'Tip' },
  important: { fill: 'FFF7ED', bar: 'D97706', label: 'Important' },
};
const PAGE_W = 9360; // 6.5in text width in DXA

const srcDir = __dirname;
const md = fs.readFileSync(path.join(srcDir, 'plan.md'), 'utf8');
const outPath = process.argv[2] || path.join(srcDir, '..', 'RelayHub-Project-Plan.docx');

// ---------- inline formatting ----------
function inline(text, base = {}) {
  const runs = [];
  const re = /(\\\*|\*\*(.+?)\*\*|`(.+?)`|\*(.+?)\*|\[([^\]]+)\]\((https?:[^)]+)\))/g;
  let last = 0, m;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) runs.push(new TextRun({ ...base, text: text.slice(last, m.index) }));
    if (m[0] === '\\*') runs.push(new TextRun({ ...base, text: '*' }));
    else if (m[2] !== undefined) runs.push(new TextRun({ ...base, text: m[2], bold: true }));
    else if (m[3] !== undefined) runs.push(new TextRun({ ...base, text: m[3], font: 'Consolas', size: (base.size || 22) - 2 }));
    else if (m[4] !== undefined) runs.push(new TextRun({ ...base, text: m[4], italics: true }));
    else if (m[5] !== undefined) runs.push(new ExternalHyperlink({ link: m[6], children: [new TextRun({ ...base, text: m[5], color: C.blue, underline: {} })] }));
    last = m.index + m[0].length;
  }
  if (last < text.length) runs.push(new TextRun({ ...base, text: text.slice(last) }));
  return runs;
}

// ---------- block renderers ----------
const para = (text, opts = {}, base = {}) => new Paragraph({ spacing: { after: 120, line: 276 }, ...opts, children: inline(text, base) });
const spacer = () => new Paragraph({ spacing: { after: 0 }, children: [new TextRun({ text: '', size: 8 })] });

function boxParas(kind, title, lines) {
  const k = C[kind];
  const label = (title || k.label) + ': ';
  return lines.filter(l => l.trim() !== '').map((l, idx) => new Paragraph({
    shading: { type: ShadingType.CLEAR, fill: k.fill, color: 'auto' },
    border: { left: { style: BorderStyle.SINGLE, size: 18, color: k.bar, space: 8 } },
    indent: { left: 120 },
    spacing: { before: idx === 0 ? 120 : 0, after: 120, line: 276 },
    children: [...(idx === 0 ? [new TextRun({ text: label, bold: true, color: k.bar })] : []), ...inline(l)],
  }));
}

function codeParas(lines) {
  return lines.map(l => new Paragraph({
    shading: { type: ShadingType.CLEAR, fill: C.code, color: 'auto' },
    border: { left: { style: BorderStyle.SINGLE, size: 18, color: C.border, space: 8 } },
    indent: { left: 120 },
    spacing: { before: 0, after: 0, line: 260 },
    children: [new TextRun({ text: l === '' ? ' ' : l, font: 'Consolas', size: 18 })],
  }));
}

function tableBlock(rowLines, widthsPct, noHeader) {
  const rows = rowLines.map(r => r.replace(/^\|/, '').replace(/\|\s*$/, '').split('|').map(c => c.trim()));
  const cols = Math.max(...rows.map(r => r.length));
  const pct = widthsPct && widthsPct.length === cols ? widthsPct : Array(cols).fill(100 / cols);
  const widths = pct.map(p => Math.round(PAGE_W * p / 100));
  widths[cols - 1] += PAGE_W - widths.reduce((a, b) => a + b, 0);
  const b = { style: BorderStyle.SINGLE, size: 4, color: C.border };
  let dataIdx = 0;
  const trs = rows.map((cells, ri) => {
    const isHeader = !noHeader && ri === 0;
    const isSection = !isHeader && cells.length > 1 && cells.every(c => c === cells[0]) && cells[0] !== '';
    const margins = { top: 70, bottom: 70, left: 100, right: 100 };
    if (isSection) {
      return new TableRow({ cantSplit: true, children: [new TableCell({
        columnSpan: cols, width: { size: PAGE_W, type: WidthType.DXA }, margins,
        shading: { type: ShadingType.CLEAR, fill: C.section, color: 'auto' },
        children: [new Paragraph({ spacing: { after: 0 }, children: [new TextRun({ text: cells[0].replace(/\*\*/g, ''), bold: true, color: C.navy, size: 19 })] })],
      })] });
    }
    const fill = isHeader ? C.navy : (dataIdx++ % 2 === 1 ? C.zebra : 'FFFFFF');
    return new TableRow({ tableHeader: isHeader, cantSplit: true, children: Array.from({ length: cols }, (_, ci) => {
      const txt = cells[ci] || '';
      const base = isHeader ? { bold: true, color: 'FFFFFF', size: 19 } : { size: 19 };
      return new TableCell({
        width: { size: widths[ci], type: WidthType.DXA }, margins, verticalAlign: VerticalAlign.CENTER,
        shading: { type: ShadingType.CLEAR, fill, color: 'auto' },
        children: [new Paragraph({ spacing: { after: 0, line: 250 }, children: inline(isHeader ? txt.replace(/\*\*/g, '') : txt, base) })],
      });
    }) });
  });
  return new Table({
    width: { size: PAGE_W, type: WidthType.DXA }, columnWidths: widths,
    borders: { top: b, bottom: b, left: b, right: b, insideHorizontal: b, insideVertical: b },
    rows: trs,
  });
}

function coverBlock(lines) {
  const out = [];
  let subtitle = '', blurb = '';
  const rows = [];
  for (const l of lines) {
    if (l.startsWith('subtitle:')) subtitle = l.slice(9).trim();
    else if (l.startsWith('blurb:')) blurb = l.slice(6).trim();
    else if (l.includes('|')) rows.push(l.split('|').map(s => s.trim()));
  }
  out.push(new Paragraph({ spacing: { before: 1800, after: 120 }, children: [new TextRun({ text: 'PROJECT PLAN', bold: true, color: C.blue, characterSpacing: 60, size: 24 })] }));
  out.push(new Paragraph({ spacing: { after: 80 }, children: [new TextRun({ text: 'RelayHub', bold: true, color: C.navy, size: 84 })] }));
  out.push(new Paragraph({
    border: { bottom: { style: BorderStyle.SINGLE, size: 18, space: 12, color: C.blue } },
    spacing: { after: 360 }, children: [new TextRun({ text: subtitle, color: C.slate, size: 36 })],
  }));
  out.push(new Paragraph({ spacing: { after: 600, line: 320 }, children: inline(blurb, { size: 24 }) }));
  const none = { style: BorderStyle.NONE, size: 0, color: 'FFFFFF' };
  const line = { style: BorderStyle.SINGLE, size: 4, color: C.headBorder };
  out.push(new Table({
    width: { size: PAGE_W, type: WidthType.DXA }, columnWidths: [2200, 7160],
    borders: { top: none, bottom: none, left: none, right: none, insideVertical: none, insideHorizontal: line },
    rows: rows.map(([k, v]) => new TableRow({ children: [
      new TableCell({ width: { size: 2200, type: WidthType.DXA }, margins: { top: 60, bottom: 60, left: 0, right: 100 }, children: [new Paragraph({ spacing: { after: 0 }, children: [new TextRun({ text: k, bold: true, color: C.navy, size: 20 })] })] }),
      new TableCell({ width: { size: 7160, type: WidthType.DXA }, margins: { top: 60, bottom: 60, left: 100, right: 0 }, children: [new Paragraph({ spacing: { after: 0 }, children: inline(v, { size: 20 }) })] }),
    ] })),
  }));
  out.push(new Paragraph({ children: [new PageBreak()] }));
  return out;
}

// ---------- parser ----------
function parse(src) {
  const lines = src.split(/\r?\n/);
  const out = [];
  let i = 0, listInstance = 0, inNumbered = false, widths = null, noHeader = false;
  while (i < lines.length) {
    const line = lines[i];
    let m;
    if ((m = line.match(/^<!--\s*widths:\s*([\d.,\s]+)-->/))) { widths = m[1].split(',').map(Number); i++; continue; }
    if (/^<!--\s*noheader/.test(line)) { noHeader = true; i++; continue; }
    if (/^<!--/.test(line)) { i++; continue; }
    if (line.trim() === '') { inNumbered = false; i++; continue; }
    if (line.startsWith(':::')) {
      const head = line.slice(3).trim();
      const body = [];
      i++;
      while (i < lines.length && lines[i].trim() !== ':::') { body.push(lines[i]); i++; }
      i++;
      const [kind, ...rest] = head.split(' ');
      const title = rest.join(' ').trim();
      if (kind === 'cover') out.push(...coverBlock(body));
      else if (kind === 'code') out.push(...codeParas(body), spacer());
      else out.push(...boxParas(kind, title, body));
      inNumbered = false;
      continue;
    }
    if (line.startsWith('|')) {
      const rows = [];
      while (i < lines.length && lines[i].startsWith('|')) { rows.push(lines[i]); i++; }
      out.push(tableBlock(rows, widths, noHeader), spacer());
      widths = null; noHeader = false; inNumbered = false;
      continue;
    }
    if (line === '[[TOC]]') {
      out.push(new Paragraph({ spacing: { after: 120 }, children: [new TextRun({ text: 'Contents', bold: true, color: C.navy, size: 28 })] }));
      out.push(new TableOfContents('Contents', { hyperlink: true, headingStyleRange: '1-1' }));
      i++; continue;
    }
    if (line === '[[PAGEBREAK]]') { out.push(new Paragraph({ children: [new PageBreak()] })); i++; continue; }
    if ((m = line.match(/^!\[(.*)\]\((.+)\)$/))) {
      const img = fs.readFileSync(path.resolve(srcDir, m[2]));
      out.push(new Paragraph({ alignment: AlignmentType.CENTER, spacing: { before: 120, after: 80 }, children: [new ImageRun({ type: 'png', data: img, transformation: { width: 624, height: 370 } })] }));
      if (m[1]) out.push(new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 200 }, children: [new TextRun({ text: m[1], italics: true, color: C.slate, size: 18 })] }));
      i++; continue;
    }
    if ((m = line.match(/^(#{1,3}) (.+)$/))) {
      const lvl = [HeadingLevel.HEADING_1, HeadingLevel.HEADING_2, HeadingLevel.HEADING_3][m[1].length - 1];
      out.push(new Paragraph({ heading: lvl, children: [new TextRun({ text: m[2] })] }));
      inNumbered = false; i++; continue;
    }
    if ((m = line.match(/^- (.+)$/))) {
      out.push(new Paragraph({ numbering: { reference: 'bullets', level: 0 }, spacing: { after: 80, line: 276 }, children: inline(m[1]) }));
      inNumbered = false; i++; continue;
    }
    if ((m = line.match(/^\d+\. (.+)$/))) {
      if (!inNumbered) { listInstance++; inNumbered = true; }
      out.push(new Paragraph({ numbering: { reference: 'numbers', level: 0, instance: listInstance }, spacing: { after: 80, line: 276 }, children: inline(m[1]) }));
      i++; continue;
    }
    out.push(para(line));
    inNumbered = false; i++;
  }
  return out;
}

// ---------- document ----------
const children = parse(md);
const footerText = { color: C.slate, size: 18 };
const doc = new Document({
  creator: 'Tim Tran', title: 'RelayHub — Project Plan',
  features: { updateFields: true },
  styles: {
    default: { document: { run: { font: 'Calibri', size: 22, color: C.text } } },
    paragraphStyles: [
      { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { bold: true, color: C.navy, size: 34 },
        paragraph: { spacing: { before: 520, after: 200 }, keepNext: true, keepLines: true, outlineLevel: 0,
          border: { bottom: { style: BorderStyle.SINGLE, size: 8, space: 6, color: C.headBorder } } } },
      { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { bold: true, color: C.blue, size: 26 },
        paragraph: { spacing: { before: 280, after: 120 }, keepNext: true, keepLines: true, outlineLevel: 1 } },
      { id: 'Heading3', name: 'Heading 3', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { bold: true, color: C.navy, size: 23 },
        paragraph: { spacing: { before: 200, after: 100 }, keepNext: true, keepLines: true, outlineLevel: 2 } },
    ],
  },
  numbering: { config: [
    { reference: 'bullets', levels: [{ level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } }] },
    { reference: 'numbers', levels: [{ level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 330 } } } }] },
  ] },
  sections: [{
    properties: { page: { size: { width: 12240, height: 15840 }, margin: { top: 1440, right: 1440, bottom: 1440, left: 1440, header: 708, footer: 708 } }, titlePage: true },
    footers: {
      default: new Footer({ children: [new Paragraph({ tabStops: [{ type: TabStopType.RIGHT, position: PAGE_W }], children: [
        new TextRun({ ...footerText, text: 'RelayHub — Project Plan' }),
        new TextRun({ ...footerText, children: ['\t', 'Page ', PageNumber.CURRENT, ' of ', PageNumber.TOTAL_PAGES] }),
      ] })] }),
      first: new Footer({ children: [new Paragraph({ children: [] })] }),
    },
    children,
  }],
});

Packer.toBuffer(doc).then(buf => {
  fs.writeFileSync(outPath, buf);
  console.log('wrote', outPath, buf.length, 'bytes');
});
