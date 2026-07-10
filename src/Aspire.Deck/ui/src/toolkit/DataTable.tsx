import type { CSSProperties, Key, ReactNode } from "react";
import {
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from "@fluentui/react-components";

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  width?: string;
  minWidth?: string;
}

function getColumnStyle<T>(column: Column<T>): CSSProperties {
  return {
    width: column.width,
    minWidth: column.minWidth ?? column.width ?? "120px",
  };
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
  const tableMinWidth = columns.length > 0
    ? `calc(${columns.map((column) => column.minWidth ?? column.width ?? "120px").join(" + ")})`
    : undefined;

  return (
    <div className="table-wrap">
      <Table className="data" size="small" style={{ minWidth: tableMinWidth }}>
        <TableHeader>
          <TableRow>
            {columns.map((column) => (
              <TableHeaderCell key={column.key} style={getColumnStyle(column)}>
                {column.header}
              </TableHeaderCell>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.length === 0 ? (
            <TableRow>
              <TableCell colSpan={columns.length} className="cell-muted" style={{ textAlign: "center", padding: "28px" }}>
                {emptyMessage}
              </TableCell>
            </TableRow>
          ) : (
            rows.map((row) => {
              const selected = isSelected?.(row) ?? false;
              const extra = rowClassName?.(row) ?? "";
              return (
                <TableRow
                  key={rowKey(row)}
                  className={`${onRowClick ? "clickable" : ""} ${selected ? "selected" : ""} ${extra}`.trim()}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                >
                  {columns.map((column) => (
                    <TableCell key={column.key} style={getColumnStyle(column)}>{column.render(row)}</TableCell>
                  ))}
                </TableRow>
              );
            })
          )}
        </TableBody>
      </Table>
    </div>
  );
}
