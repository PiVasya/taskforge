import React, { useMemo, useState } from 'react';
import {
  columnFilteringFeature,
  columnResizingFeature,
  columnSizingFeature,
  createFilteredRowModel,
  createPaginatedRowModel,
  createSortedRowModel,
  filterFn_includesString,
  flexRender,
  globalFilteringFeature,
  rowPaginationFeature,
  rowSortingFeature,
  sortFn_alphanumeric,
  tableFeatures,
  useTable,
} from '@tanstack/react-table';
import { ChevronLeft, ChevronRight, Search } from 'lucide-react';
import { Button, Card, Input, Select } from '../../components/ui';
import { cellText } from './sqlModel';
import './sql-database.css';

const databaseTableFeatures = tableFeatures({
  columnFilteringFeature,
  globalFilteringFeature,
  rowSortingFeature,
  rowPaginationFeature,
  columnSizingFeature,
  columnResizingFeature,
  filteredRowModel: createFilteredRowModel(),
  sortedRowModel: createSortedRowModel(),
  paginatedRowModel: createPaginatedRowModel(),
  filterFns: { includesString: filterFn_includesString },
  sortFns: { alphanumeric: sortFn_alphanumeric },
});

function typeLabel(column) {
  const type = column?.nativeType || column?.type || column?.logicalType || '';
  return column?.length ? `${type}(${column.length})` : type;
}

function tableRows(table, seed) {
  return (seed?.[table.name] || []).map((row, index) => ({ ...row, __row: index + 1 }));
}

export default function SqlDatabaseViewer({ definition, seed }) {
  const tables = definition?.tables || [];
  const [tableName, setTableName] = useState(tables[0]?.name || '');
  const [globalFilter, setGlobalFilter] = useState('');
  const [sorting, setSorting] = useState([]);
  const [pagination, setPagination] = useState({ pageIndex: 0, pageSize: 50 });

  const selected = tables.find(table => table.name === tableName) || tables[0] || null;
  const data = useMemo(() => selected ? tableRows(selected, seed) : [], [selected, seed]);
  const columns = useMemo(() => {
    if (!selected) return [];
    return [
      {
        id: '__row',
        accessorKey: '__row',
        header: '#',
        size: 64,
        enableSorting: false,
        enableGlobalFilter: false,
        cell: info => info.getValue(),
      },
      ...selected.columns.map(column => ({
        id: column.name,
        accessorFn: row => Object.hasOwn(row, column.name) ? cellText(row[column.name]) : 'DEFAULT',
        header: () => (
          <span className="sql-db-column-heading">
            <strong>{column.name}</strong>
            <small>{typeLabel(column)}</small>
          </span>
        ),
        cell: info => {
          const value = info.getValue();
          return <span className={value === 'NULL' ? 'sql-db-null' : ''}>{value}</span>;
        },
        sortFn: 'alphanumeric',
        minSize: 120,
        size: 180,
      })),
    ];
  }, [selected]);

  const table = useTable({
    features: databaseTableFeatures,
    data,
    columns,
    state: { globalFilter, sorting, pagination },
    onGlobalFilterChange: setGlobalFilter,
    onSortingChange: setSorting,
    onPaginationChange: setPagination,
    globalFilterFn: 'includesString',
    columnResizeMode: 'onChange',
  });

  const switchTable = name => {
    setTableName(name);
    setGlobalFilter('');
    setSorting([]);
    setPagination(current => ({ ...current, pageIndex: 0 }));
  };

  if (!tables.length) return <Card>Таблиц нет</Card>;

  const filteredRows = table.getFilteredRowModel().rows.length;
  const totalRows = data.length;
  const pageCount = Math.max(1, table.getPageCount());

  return (
    <div className="sql-db-layout">
      <Card className="sql-db-sidebar">
        <div className="sql-db-table-list" role="list" aria-label="Таблицы базы данных">
          {tables.map(item => {
            const active = item.name === selected?.name;
            const count = (seed?.[item.name] || []).length;
            return (
              <button
                type="button"
                key={item.name}
                className={`sql-db-table-item${active ? ' is-active' : ''}`}
                onClick={() => switchTable(item.name)}
              >
                <span>{item.name}</span>
                <span>{count}</span>
              </button>
            );
          })}
        </div>
      </Card>

      <Card className="sql-db-main">
        <div className="sql-db-toolbar">
          <div className="sql-db-title-row">
            <h1>{selected?.name}</h1>
            <span className="sql-db-row-count">{globalFilter ? `${filteredRows} / ${totalRows}` : totalRows}</span>
          </div>
          <div className="sql-db-controls">
            <label className="sql-db-search">
              <Search size={16} aria-hidden="true" />
              <Input
                aria-label="Поиск по таблице"
                placeholder="Поиск"
                value={globalFilter ?? ''}
                onChange={event => setGlobalFilter(event.target.value)}
              />
            </label>
            <Select
              className="sql-db-page-size"
              aria-label="Строк на странице"
              value={pagination.pageSize >= Math.max(1, totalRows) && totalRows > 250 ? 'all' : String(pagination.pageSize)}
              onChange={event => {
                const value = event.target.value;
                setPagination({ pageIndex: 0, pageSize: value === 'all' ? Math.max(1, totalRows) : Number(value) });
              }}
            >
              {[25, 50, 100, 250].map(size => <option key={size} value={size}>{size}</option>)}
              {totalRows > 250 ? <option value="all">Все</option> : null}
            </Select>
          </div>
        </div>

        <div className="sql-db-grid-wrap">
          <table className="sql-db-grid" style={{ width: table.getCenterTotalSize() }}>
            <thead>
              {table.getHeaderGroups().map(headerGroup => (
                <tr key={headerGroup.id}>
                  {headerGroup.headers.map(header => (
                    <th key={header.id} style={{ width: header.getSize() }}>
                      <button
                        type="button"
                        className={`sql-db-sort${header.column.getCanSort() ? ' can-sort' : ''}`}
                        onClick={header.column.getToggleSortingHandler()}
                        disabled={!header.column.getCanSort()}
                      >
                        {header.isPlaceholder ? null : flexRender(header.column.columnDef.header, header.getContext())}
                        {header.column.getIsSorted() === 'asc' ? <span aria-hidden="true">↑</span> : null}
                        {header.column.getIsSorted() === 'desc' ? <span aria-hidden="true">↓</span> : null}
                      </button>
                      {header.column.getCanResize() ? (
                        <div
                          className={`sql-db-resizer${header.column.getIsResizing() ? ' is-resizing' : ''}`}
                          onMouseDown={header.getResizeHandler()}
                          onTouchStart={header.getResizeHandler()}
                        />
                      ) : null}
                    </th>
                  ))}
                </tr>
              ))}
            </thead>
            <tbody>
              {table.getRowModel().rows.map(row => (
                <tr key={row.id}>
                  {row.getAllCells().map(cell => (
                    <td key={cell.id} style={{ width: cell.column.getSize() }}>
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
          {!table.getRowModel().rows.length ? <div className="sql-db-empty">Нет строк</div> : null}
        </div>

        <div className="sql-db-footer">
          <span>{pagination.pageIndex + 1} / {pageCount}</span>
          <div className="sql-db-pagination">
            <Button
              type="button"
              variant="outline"
              aria-label="Предыдущая страница"
              disabled={!table.getCanPreviousPage()}
              onClick={() => table.previousPage()}
            >
              <ChevronLeft size={17} />
            </Button>
            <Button
              type="button"
              variant="outline"
              aria-label="Следующая страница"
              disabled={!table.getCanNextPage()}
              onClick={() => table.nextPage()}
            >
              <ChevronRight size={17} />
            </Button>
          </div>
        </div>
      </Card>
    </div>
  );
}
