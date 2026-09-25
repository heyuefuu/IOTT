import type { CollectionDataType } from "../api/machineConnectionCollection";

export interface TiaSymbolTableRow {
  address: string;
  dataType: CollectionDataType;
  groupName: string;
  intervalMs: number;
  displayName: string;
  unit?: string;
}

export interface TiaSymbolTableParseOptions {
  defaultIntervalMs?: number;
  defaultGroupName?: string;
}

type CsvRow = string[];

const delimiterCandidates = [",", ";", "\t"] as const;

export async function readImportFileText(file: Blob): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  const encoding = bytes[0] === 0xff && bytes[1] === 0xfe
    ? "utf-16le"
    : bytes[0] === 0xfe && bytes[1] === 0xff ? "utf-16be" : "utf-8";
  try {
    return new TextDecoder(encoding, { fatal: true }).decode(bytes);
  } catch {
    if (encoding === "utf-8") {
      try {
        return new TextDecoder("gb18030", { fatal: true }).decode(bytes);
      } catch {
        throw new Error("文件编码识别失败，请使用 UTF-8、GBK 或带 BOM 的 UTF-16 文件");
      }
    }
    throw new Error("UTF-16 文件内容不完整，请重新导出");
  }
}

function detectDelimiter(content: string): string {
  const firstLine = content.split(/\r?\n/, 1)[0] ?? "";
  let delimiter = ",";
  let maxFields = -1;

  for (const candidate of delimiterCandidates) {
    const fieldCount = firstLine.split(candidate).length;
    if (fieldCount > maxFields) {
      delimiter = candidate;
      maxFields = fieldCount;
    }
  }

  return delimiter;
}

function parseCsvRows(content: string, delimiter: string): CsvRow[] {
  const rows: CsvRow[] = [];
  let row: string[] = [];
  let field = "";
  let inQuotes = false;

  for (let index = 0; index < content.length; index += 1) {
    const char = content[index];
    if (inQuotes) {
      if (char === "\"") {
        if (content[index + 1] === "\"") {
          field += "\"";
          index += 1;
        } else {
          inQuotes = false;
        }
      } else {
        field += char;
      }
      continue;
    }

    if (char === "\"") {
      inQuotes = true;
    } else if (char === delimiter) {
      row.push(field);
      field = "";
    } else if (char === "\n") {
      row.push(field);
      rows.push(row);
      row = [];
      field = "";
    } else if (char !== "\r") {
      field += char;
    }
  }

  if (field !== "" || row.length > 0) {
    row.push(field);
    rows.push(row);
  }

  return rows.filter((item) => item.some((value) => value.trim() !== ""));
}

function normalizeHeader(value: string): string {
  return value
    .replace(/^\uFEFF/, "")
    .trim()
    .toLowerCase()
    .replace(/[\s_-]+/g, "");
}

function findColumn(headers: string[], aliases: string[]): number {
  const normalizedHeaders = headers.map(normalizeHeader);
  for (const alias of aliases) {
    const index = normalizedHeaders.indexOf(normalizeHeader(alias));
    if (index >= 0) return index;
  }
  return -1;
}

function getValue(row: CsvRow, index: number): string {
  return index >= 0 && index < row.length ? (row[index] ?? "").trim() : "";
}

function mapDataType(value: string): CollectionDataType {
  const normalized = value.trim().toLowerCase().replace(/[\s_-]+/g, "");
  if (normalized.startsWith("string")) {
    return "String";
  }
  switch (normalized) {
    case "bool":
    case "boolean":
      return "Bool";
    case "byte":
    case "usint":
    case "char":
      return "UInt8";
    case "sint":
      return "Int8";
    case "word":
    case "uint":
    case "wchar":
    case "date":
      return "UInt16";
    case "int":
      return "Int16";
    case "dint":
    case "time":
      return "Int32";
    case "udint":
    case "dword":
    case "timeofday":
    case "tod":
      return "UInt32";
    case "lint":
      return "Int64";
    case "ulint":
    case "lword":
      return "UInt64";
    case "real":
      return "Float";
    case "lreal":
      return "Double";
    default:
      throw new Error(`TIA 数据类型未受支持：${value || "(空)"}`);
  }
}

function escapeCsvValue(value: string): string {
  return /[",;\r\n]/.test(value)
    ? `"${value.replace(/"/g, "\"\"")}"`
    : value;
}

export function parseTiaSymbolTableCsv(
  content: string,
  options: TiaSymbolTableParseOptions = {},
): TiaSymbolTableRow[] {
  const normalizedContent = content.replace(/^\uFEFF/, "");
  const delimiter = detectDelimiter(normalizedContent);
  const rows = parseCsvRows(normalizedContent, delimiter);
  if (rows.length === 0) return [];

  const headers = rows[0] ?? [];
  const nameIndex = findColumn(headers, [
    "Name",
    "Symbol",
    "Symbol Name",
    "Variable",
    "Variable Name",
    "Tag",
    "Tag Name",
    "名称",
    "变量名",
    "符号名",
  ]);
  const addressIndex = findColumn(headers, [
    "Address",
    "Absolute Address",
    "Logical Address",
    "地址",
    "绝对地址",
    "逻辑地址",
  ]);
  const dataTypeIndex = findColumn(headers, [
    "Data Type",
    "DataType",
    "Type",
    "数据类型",
    "类型",
  ]);
  const groupIndex = findColumn(headers, [
    "Group",
    "Group Name",
    "GroupName",
    "分组",
    "组名",
  ]);
  const unitIndex = findColumn(headers, [
    "Unit",
    "Engineering Unit",
    "单位",
  ]);

  if (nameIndex < 0 || addressIndex < 0 || dataTypeIndex < 0) {
    throw new Error(
      "TIA 符号表缺少必需列：Name、Data Type、Address 至少各一列",
    );
  }

  const defaultIntervalMs = options.defaultIntervalMs ?? 1000;
  const defaultGroupName = options.defaultGroupName ?? "TIA Import";

  const result: TiaSymbolTableRow[] = [];

  for (const row of rows.slice(1)) {
    const address = getValue(row, addressIndex).replace(/^%/, "");
    if (!address) continue;

    const displayName = getValue(row, nameIndex) || address;
    let dataType: CollectionDataType;
    try {
      dataType = mapDataType(getValue(row, dataTypeIndex));
    } catch (error) {
      throw new Error(`${displayName} (${address})：${error instanceof Error ? error.message : String(error)}`);
    }
    result.push({
      address,
      dataType,
      groupName: getValue(row, groupIndex) || defaultGroupName,
      intervalMs: defaultIntervalMs,
      displayName,
      unit: getValue(row, unitIndex) || undefined,
    });
  }

  return result;
}

export function toCollectionImportCsv(rows: TiaSymbolTableRow[]): string {
  const header = ["Address", "DataType", "GroupName", "IntervalMs", "DisplayName", "Unit"];
  const lines = [header.join(",")];
  for (const row of rows) {
    lines.push(
      [
        row.address,
        row.dataType,
        row.groupName,
        String(row.intervalMs),
        row.displayName,
        row.unit ?? "",
      ]
        .map(escapeCsvValue)
        .join(","),
    );
  }
  return lines.join("\n");
}

export function toCollectionImportCsvFile(
  rows: TiaSymbolTableRow[],
  fileName: string,
): File {
  return new File([toCollectionImportCsv(rows)], fileName, {
    type: "text/csv",
  });
}
