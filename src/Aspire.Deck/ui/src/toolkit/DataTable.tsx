import { useMemo, useState, type CSSProperties, type Key, type ReactNode } from "react";
import {
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from "@fluentui/react-components";
import { SortAscendingIcon, SortDescendingIcon } from "./Icons";

export type SortDirection = "ascending" | "descending";

export interface DataTableSort {
  columnKey: string;
  direction: SortDirection;
}

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  width?: string;
  minWidth?: string;
  compare?: (left: T, right: T) => number;
  compareWithDirection?: (left: T, right: T, direction: SortDirection) => number;
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
  sort,
  defaultSort,
  onSortChange,
}: {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => Key;
  onRowClick?: (row: T) => void;
  isSelected?: (row: T) => boolean;
  rowClassName?: (row: T) => string | undefined;
  emptyMessage?: string;
  sort?: DataTableSort | null;
  defaultSort?: DataTableSort | null;
  onSortChange?: (sort: DataTableSort) => void;
}) {
  const [internalSort, setInternalSort] = useState<DataTableSort | null>(defaultSort ?? null);
  const activeSort = sort === undefined ? internalSort : sort;
  const tableMinWidth = columns.length > 0
    ? `calc(${columns.map((column) => column.minWidth ?? column.width ?? "120px").join(" + ")})`
    : undefined;
  const displayedRows = useMemo(() => {
    if (!activeSort) {
      return rows;
    }

    const column = columns.find((candidate) => candidate.key === activeSort.columnKey);
    const compare = column?.compare;
    const compareWithDirection = column?.compareWithDirection;
    if (!compare && !compareWithDirection) {
      return rows;
    }

    const direction = activeSort.direction === "ascending" ? 1 : -1;
    return rows
      .map((row, index) => ({ row, index }))
      .sort((left, right) => (compareWithDirection
        ? compareWithDirection(left.row, right.row, activeSort.direction)
        : direction * compare!(left.row, right.row)) || left.index - right.index)
      .map((item) => item.row);
  }, [activeSort, columns, rows]);

  const changeSort = (column: Column<T>): void => {
    if (!column.compare && !column.compareWithDirection) {
      return;
    }

    const next: DataTableSort = {
      columnKey: column.key,
      direction:
        activeSort?.columnKey === column.key && activeSort.direction === "ascending"
          ? "descending"
          : "ascending",
    };
    if (sort === undefined) {
      setInternalSort(next);
    }
    onSortChange?.(next);
  };

  return (
    <div className="table-wrap">
      <Table className="data" size="small" style={{ minWidth: tableMinWidth }}>
        <TableHeader>
          <TableRow>
            {columns.map((column) => {
              const direction = activeSort?.columnKey === column.key ? activeSort.direction : undefined;
              return (
                <TableHeaderCell
                  key={column.key}
                  style={getColumnStyle(column)}
                  aria-sort={direction}
                >
                  {column.compare || column.compareWithDirection ? (
                    <button className="data__sort" type="button" onClick={() => changeSort(column)}>
                      <span>{column.header}</span>
                      {direction === "ascending" ? <SortAscendingIcon size={14} /> : null}
                      {direction === "descending" ? <SortDescendingIcon size={14} /> : null}
                    </button>
                  ) : (
                    column.header
                  )}
                </TableHeaderCell>
              );
            })}
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
            displayedRows.map((row) => {
              const selected = isSelected?.(row) ?? false;
              const extra = rowClassName?.(row) ?? "";
              return (
                <TableRow
                  key={rowKey(row)}
                  className={`${onRowClick ? "clickable" : ""} ${selected ? "selected" : ""} ${extra}`.trim()}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  onKeyDown={onRowClick
                    ? (event) => {
                        if (event.key === "Enter" || event.key === " ") {
                          event.preventDefault();
                          onRowClick(row);
                        }
                      }
                    : undefined}
                  tabIndex={onRowClick ? 0 : undefined}
                  aria-selected={isSelected ? selected : undefined}
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
