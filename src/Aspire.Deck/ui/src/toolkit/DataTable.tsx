import type { Key, ReactNode } from "react";
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
      <Table className="data" size="small">
        <TableHeader>
          <TableRow>
            {columns.map((column) => (
              <TableHeaderCell key={column.key} style={column.width ? { width: column.width } : undefined}>
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
                    <TableCell key={column.key}>{column.render(row)}</TableCell>
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
