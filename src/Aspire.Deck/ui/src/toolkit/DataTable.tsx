import { useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type Key, type ReactNode, type UIEvent } from "react";
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
  onRowContextMenu,
  isSelected,
  rowClassName,
  emptyMessage = "No rows.",
  sort,
  defaultSort,
  onSortChange,
  virtualizeAbove,
  virtualRowHeight = 45,
  virtualOverscan = 12,
  virtualHeight = "100%",
}: {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => Key;
  onRowClick?: (row: T) => void;
  onRowContextMenu?: (row: T, x: number, y: number) => void;
  isSelected?: (row: T) => boolean;
  rowClassName?: (row: T) => string | undefined;
  emptyMessage?: string;
  sort?: DataTableSort | null;
  defaultSort?: DataTableSort | null;
  onSortChange?: (sort: DataTableSort) => void;
  virtualizeAbove?: number;
  virtualRowHeight?: number;
  virtualOverscan?: number;
  virtualHeight?: CSSProperties["height"];
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
  const virtual = virtualizeAbove !== undefined && displayedRows.length > virtualizeAbove;
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(0);
  useLayoutEffect(() => {
    const wrapper = wrapperRef.current;
    if (!virtual || wrapper === null) return;
    const update = () => setViewportHeight(wrapper.clientHeight);
    update();
    const observer = new ResizeObserver(update);
    observer.observe(wrapper);
    return () => observer.disconnect();
  }, [virtual]);
  const virtualWindow = useMemo(() => {
    if (!virtual) return { rows: displayedRows, start: 0, end: displayedRows.length };
    const visibleCount = Math.ceil((viewportHeight || 600) / virtualRowHeight);
    const start = Math.max(0, Math.min(
      Math.floor(scrollTop / virtualRowHeight) - virtualOverscan,
      Math.max(0, displayedRows.length - visibleCount),
    ));
    const end = Math.min(displayedRows.length, start + visibleCount + virtualOverscan * 2);
    return { rows: displayedRows.slice(start, end), start, end };
  }, [displayedRows, scrollTop, viewportHeight, virtual, virtualOverscan, virtualRowHeight]);

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
    <div
      ref={wrapperRef}
      className={`table-wrap ${virtual ? "table-wrap--virtual" : ""}`.trim()}
      style={virtual ? { height: virtualHeight } : undefined}
      data-virtualized={virtual ? "true" : undefined}
      onScroll={virtual ? (event: UIEvent<HTMLDivElement>) => setScrollTop(event.currentTarget.scrollTop) : undefined}
    >
      <Table className="data" size="small" style={{ minWidth: tableMinWidth }} aria-rowcount={displayedRows.length + 1}>
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
            <>
            {virtualWindow.start > 0 ? (
              <TableRow aria-hidden="true" className="data__virtual-spacer">
                <TableCell colSpan={columns.length} style={{ height: virtualWindow.start * virtualRowHeight }} />
              </TableRow>
            ) : null}
            {virtualWindow.rows.map((row) => {
              const selected = isSelected?.(row) ?? false;
              const extra = rowClassName?.(row) ?? "";
              return (
                <TableRow
                  key={rowKey(row)}
                  className={`${onRowClick ? "clickable" : ""} ${selected ? "selected" : ""} ${extra}`.trim()}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  onContextMenu={onRowContextMenu ? (event) => {
                    event.preventDefault();
                    onRowContextMenu(row, event.clientX, event.clientY);
                  } : undefined}
                  onKeyDown={onRowClick || onRowContextMenu
                    ? (event) => {
                        if (onRowClick && (event.key === "Enter" || event.key === " ")) {
                          event.preventDefault();
                          onRowClick(row);
                        } else if (onRowContextMenu && (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10"))) {
                          event.preventDefault();
                          const bounds = event.currentTarget.getBoundingClientRect();
                          onRowContextMenu(row, bounds.left + 24, bounds.top + 24);
                        }
                      }
                    : undefined}
                  tabIndex={onRowClick || onRowContextMenu ? 0 : undefined}
                  aria-selected={isSelected ? selected : undefined}
                >
                  {columns.map((column) => (
                    <TableCell key={column.key} style={getColumnStyle(column)}>{column.render(row)}</TableCell>
                  ))}
                </TableRow>
              );
            })}
            {virtualWindow.end < displayedRows.length ? (
              <TableRow aria-hidden="true" className="data__virtual-spacer">
                <TableCell colSpan={columns.length} style={{ height: (displayedRows.length - virtualWindow.end) * virtualRowHeight }} />
              </TableRow>
            ) : null}
            </>
          )}
        </TableBody>
      </Table>
    </div>
  );
}
