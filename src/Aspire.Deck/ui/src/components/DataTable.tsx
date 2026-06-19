import type { Key, ReactNode } from "react";

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  width?: string;
}

export function DataTable<T>({
  columns,
  rows,
  rowKey,
  onRowClick,
  isSelected,
  rowClassName,
  emptyMessage = "No rows.",
}: {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => Key;
  onRowClick?: (row: T) => void;
  isSelected?: (row: T) => boolean;
  rowClassName?: (row: T) => string | undefined;
  emptyMessage?: string;
}) {
  return (
    <div className="table-wrap">
      <table className="data">
        <thead>
          <tr>
            {columns.map((col) => (
              <th key={col.key} style={col.width ? { width: col.width } : undefined}>
                {col.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length} className="cell-muted" style={{ textAlign: "center", padding: "28px" }}>
                {emptyMessage}
              </td>
            </tr>
          ) : (
            rows.map((row) => {
              const selected = isSelected?.(row) ?? false;
              const extra = rowClassName?.(row) ?? "";
              return (
                <tr
                  key={rowKey(row)}
                  className={`${onRowClick ? "clickable" : ""} ${selected ? "selected" : ""} ${extra}`.trim()}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                >
                  {columns.map((col) => (
                    <td key={col.key}>{col.render(row)}</td>
                  ))}
                </tr>
              );
            })
          )}
        </tbody>
      </table>
    </div>
  );
}
