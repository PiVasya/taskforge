import React from 'react';
import { Button, Card } from '../../../components/ui';
import { ShortcutInstructions, ShortcutList, useShortcuts } from '../../shortcuts/ShortcutProvider';

export default function ShortcutSettingsSection() {
  const shortcuts = useShortcuts();
  if (!shortcuts) return null;
  return <Card className="tf-shortcuts-settings">
    <h2 className="text-xl font-semibold">{'Горячие клавиши'}</h2>
    <label><input type="checkbox" checked={shortcuts.enabled} onChange={(event) => shortcuts.updateSettings({ enabled: event.target.checked })} />{'Включить горячие клавиши'}</label>
    <label><input type="checkbox" checked={shortcuts.hints} disabled={!shortcuts.enabled} onChange={(event) => shortcuts.updateSettings({ hints: event.target.checked })} />{'Показывать подсказки при удержании Alt'}</label>
    <div className="flex flex-wrap gap-2">
      <Button variant="outline" onClick={shortcuts.toggleHints}>{shortcuts.pinned ? 'Скрыть комбинации' : 'Показать комбинации'}</Button>
      <Button variant="outline" shortcut="help" onClick={shortcuts.showHelp}>{'Справочник'}</Button>
    </div>
    <ShortcutInstructions />
    <p className="text-xs opacity-70">{'Эти настройки сохраняются сразу в этом браузере.'}</p>
    <ShortcutList permissions={shortcuts.permissions} />
  </Card>;
}
