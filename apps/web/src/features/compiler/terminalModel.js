const MAX_LINES = 5000;
const MAX_COLUMNS = 2000;

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function replaceAt(value, index, character) {
  if (index >= MAX_COLUMNS) return value;
  if (index >= value.length) return value.padEnd(index, ' ') + character;
  return value.slice(0, index) + character + value.slice(index + 1);
}

export class TerminalScreenModel {
  constructor() {
    this.clear();
  }

  clear() {
    this.lines = [''];
    this.row = 0;
    this.column = 0;
    this.savedRow = 0;
    this.savedColumn = 0;
    this.cursorVisible = true;
    this.pendingControl = '';
  }

  ensureRow(row) {
    while (this.lines.length <= row) this.lines.push('');
  }

  trimHistory() {
    if (this.lines.length <= MAX_LINES) return;
    const remove = this.lines.length - MAX_LINES;
    this.lines.splice(0, remove);
    this.row = Math.max(0, this.row - remove);
    this.savedRow = Math.max(0, this.savedRow - remove);
  }

  lineFeed() {
    this.row += 1;
    this.column = 0;
    this.ensureRow(this.row);
    this.trimHistory();
  }

  writeCharacter(character) {
    if (this.column >= MAX_COLUMNS) this.lineFeed();
    this.ensureRow(this.row);
    this.lines[this.row] = replaceAt(this.lines[this.row], this.column, character);
    this.column += 1;
  }

  eraseLine(mode = 0) {
    this.ensureRow(this.row);
    const line = this.lines[this.row];
    if (mode === 2) {
      this.lines[this.row] = '';
      this.column = 0;
      return;
    }
    if (mode === 1) {
      this.lines[this.row] = ' '.repeat(this.column) + line.slice(this.column);
      return;
    }
    this.lines[this.row] = line.slice(0, this.column);
  }

  eraseDisplay(mode = 0) {
    if (mode === 2 || mode === 3) {
      this.clear();
      return;
    }
    if (mode === 1) {
      for (let index = 0; index < this.row; index += 1) this.lines[index] = '';
      this.eraseLine(1);
      return;
    }
    this.eraseLine(0);
    this.lines.splice(this.row + 1);
  }

  applyCsi(parametersText, command) {
    const privateMode = parametersText.startsWith('?');
    const clean = privateMode ? parametersText.slice(1) : parametersText;
    const parameters = clean.length
      ? clean.split(';').map((part) => Number.parseInt(part || '0', 10))
      : [0];
    const first = Number.isFinite(parameters[0]) ? parameters[0] : 0;
    const count = Math.max(1, first || 1);

    switch (command) {
      case 'A':
        this.row = Math.max(0, this.row - count);
        break;
      case 'B':
        this.row += count;
        this.ensureRow(this.row);
        break;
      case 'C':
        this.column = clamp(this.column + count, 0, MAX_COLUMNS);
        break;
      case 'D':
        this.column = Math.max(0, this.column - count);
        break;
      case 'E':
        this.row += count;
        this.column = 0;
        this.ensureRow(this.row);
        break;
      case 'F':
        this.row = Math.max(0, this.row - count);
        this.column = 0;
        break;
      case 'G':
        this.column = Math.max(0, first - 1);
        break;
      case 'H':
      case 'f': {
        const targetRow = Math.max(0, (parameters[0] || 1) - 1);
        const targetColumn = Math.max(0, (parameters[1] || 1) - 1);
        this.row = targetRow;
        this.column = targetColumn;
        this.ensureRow(this.row);
        break;
      }
      case 'J':
        this.eraseDisplay(first);
        break;
      case 'K':
        this.eraseLine(first);
        break;
      case 's':
        this.savedRow = this.row;
        this.savedColumn = this.column;
        break;
      case 'u':
        this.row = this.savedRow;
        this.column = this.savedColumn;
        this.ensureRow(this.row);
        break;
      case 'h':
        if (privateMode && first === 25) this.cursorVisible = true;
        break;
      case 'l':
        if (privateMode && first === 25) this.cursorVisible = false;
        break;
      default:
        // SGR colours and unsupported terminal controls are intentionally
        // ignored while their printable text remains visible.
        break;
    }
  }

  write(data) {
    const text = `${this.pendingControl}${String(data ?? '')}`;
    this.pendingControl = '';
    let index = 0;
    while (index < text.length) {
      const character = text[index];
      if (character === '\u001b') {
        const next = text[index + 1];
        if (next === undefined) {
          this.pendingControl = text.slice(index);
          break;
        }
        if (next === '[') {
          let end = index + 2;
          while (end < text.length && !/[A-Za-z@`~]/.test(text[end])) end += 1;
          if (end >= text.length) {
            this.pendingControl = text.slice(index);
            break;
          }
          this.applyCsi(text.slice(index + 2, end), text[end]);
          index = end + 1;
          continue;
        } else if (next === ']') {
          let end = index + 2;
          let terminated = false;
          while (end < text.length) {
            if (text[end] === '\u0007') {
              end += 1;
              terminated = true;
              break;
            }
            if (text[end] === '\u001b' && text[end + 1] === '\\') {
              end += 2;
              terminated = true;
              break;
            }
            end += 1;
          }
          if (!terminated) {
            this.pendingControl = text.slice(index);
            break;
          }
          index = end;
          continue;
        } else if (next === '7') {
          this.savedRow = this.row;
          this.savedColumn = this.column;
          index += 2;
          continue;
        } else if (next === '8') {
          this.row = this.savedRow;
          this.column = this.savedColumn;
          this.ensureRow(this.row);
          index += 2;
          continue;
        }
        index += 2;
        continue;
      }

      switch (character) {
        case '\r':
          this.column = 0;
          break;
        case '\n':
          this.lineFeed();
          break;
        case '\b':
          this.column = Math.max(0, this.column - 1);
          break;
        case '\t': {
          const target = Math.min(MAX_COLUMNS, (Math.floor(this.column / 8) + 1) * 8);
          while (this.column < target) this.writeCharacter(' ');
          break;
        }
        case '\u0007':
        case '\u0000':
          break;
        case '\u000c':
          this.clear();
          break;
        default:
          if (character >= ' ') this.writeCharacter(character);
          break;
      }
      index += 1;
    }
    this.trimHistory();
  }

  appendLine(value = '') {
    if (this.column !== 0 || this.lines[this.row]) this.lineFeed();
    this.write(String(value));
    this.lineFeed();
  }

  snapshot() {
    this.ensureRow(this.row);
    const beforeLines = this.lines.slice(0, this.row);
    const activeLine = this.lines[this.row] || '';
    const cursorColumn = Math.min(this.column, activeLine.length);
    const before = `${beforeLines.length ? `${beforeLines.join('\n')}\n` : ''}${activeLine.slice(0, cursorColumn)}`;
    const afterLines = this.lines.slice(this.row + 1);
    const after = `${activeLine.slice(cursorColumn)}${afterLines.length ? `\n${afterLines.join('\n')}` : ''}`;
    return {
      before,
      after,
      cursorVisible: this.cursorVisible,
      text: this.lines.join('\n'),
    };
  }
}
