import React from 'react';
import { Button, Card, Input } from '../../../components/ui';
import { InlineError } from './SettingsPrimitives';

function IntegrationsSettingsSection({ telegram, minecraft }) {
  return (
    <div className="space-y-4">
      <Card className="p-4 space-y-3">
        <div className="flex items-center justify-between gap-3">
          <div className="font-semibold">Telegram</div>
        </div>
        <InlineError value={telegram.error} />
        {telegram.status?.linked ? (
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="text-sm">Привязан: <b>{telegram.status.username || 'Telegram'}</b></div>
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" onClick={telegram.refresh} disabled={telegram.loading}>Обновить</Button>
              <Button variant="outline" onClick={telegram.unlink} disabled={telegram.loading}>Удалить привязку</Button>
            </div>
          </div>
        ) : (
          <div className="space-y-3">
            <div className="text-sm text-neutral-500 dark:text-neutral-400">Сгенерируй код и отправь его боту {telegram.status?.botUsername ? <b>{telegram.status.botUsername}</b> : null} в личку.</div>
            {telegram.code ? (
              <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                <div className="text-xs text-neutral-500 dark:text-neutral-400">Твой код</div>
                <div className="mt-1 font-mono text-lg tracking-wider">{telegram.code}</div>
                {telegram.expires ? <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">Действует до: {new Date(telegram.expires).toLocaleString()}</div> : null}
              </div>
            ) : null}
            <div className="flex flex-wrap gap-2">
              <Button onClick={telegram.generate} disabled={telegram.loading}>{telegram.loading ? 'Генерация…' : 'Сгенерировать код'}</Button>
              <Button variant="outline" onClick={telegram.copy} disabled={!telegram.code}>Копировать</Button>
              <Button variant="outline" onClick={telegram.refresh} disabled={telegram.loading}>Обновить</Button>
            </div>
          </div>
        )}
      </Card>

      <Card className="p-4 space-y-4">
        <div className="flex items-center justify-between gap-3">
          <div className="font-semibold">Minecraft</div>
          {minecraft.status ? <div className="text-xs text-neutral-500 dark:text-neutral-400">Привязок: {minecraft.status.linkCount ?? 0}</div> : null}
        </div>
        <InlineError value={minecraft.error} />
        {minecraft.status?.linked ? (
          <div className="space-y-3">
            <div className="max-w-xs rounded-2xl border border-[rgba(var(--border)/0.65)] px-3 py-2">
              <div className="text-xs text-neutral-500 dark:text-neutral-400">Баланс</div>
              <div className="font-semibold text-lg">{minecraft.status.minecraftBalance ?? minecraft.status.balance ?? 0}</div>
            </div>
            <div className="space-y-2">
              {(Array.isArray(minecraft.status.links) ? minecraft.status.links : []).map((link) => (
                <div key={link.id || link.nick} className="flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-[rgba(var(--border)/0.65)] px-3 py-2">
                  <div className="text-sm">Привязан ник: <b>{link.nick || 'неизвестно'}</b></div>
                  <Button variant="outline" onClick={() => minecraft.unlink(link.id, link.nick)} disabled={minecraft.loading || !link.id}>Удалить</Button>
                </div>
              ))}
            </div>
          </div>
        ) : null}
        <div className="space-y-3 border-t border-[rgba(var(--border)/0.45)] pt-4">
          <div><label htmlFor="settings-minecraft-nick" className="text-sm text-neutral-500 dark:text-neutral-400">Новый ник на сервере</label><Input id="settings-minecraft-nick" placeholder="Player_123" value={minecraft.nick} onChange={(event) => minecraft.setNick(event.target.value)} disabled={minecraft.loading} /></div>
          <div className="flex flex-wrap gap-2"><Button onClick={minecraft.request} disabled={minecraft.loading}>{minecraft.loading ? 'Отправка…' : 'Отправить код в игру'}</Button><Button variant="outline" onClick={minecraft.refresh} disabled={minecraft.loading}>Обновить</Button></div>
          {minecraft.delivery ? (
            <div className={`rounded-2xl px-3 py-2 text-xs ${minecraft.delivery.delivered ? 'bg-emerald-500/10 text-emerald-300' : 'bg-red-500/10 text-red-300'}`}>
              {minecraft.delivery.delivered ? 'Код отправлен в игру. Введите полученный код ниже.' : `Не удалось доставить код в игру: ${minecraft.delivery.message || 'неизвестная ошибка'}`}
              {minecraft.delivery.delivered && minecraft.expires ? <div className="mt-1 opacity-80">Действует до: {new Date(minecraft.expires).toLocaleString()}</div> : null}
            </div>
          ) : null}
          <div><label htmlFor="settings-minecraft-code" className="text-sm text-neutral-500 dark:text-neutral-400">Код, полученный в игре</label><Input id="settings-minecraft-code" placeholder="ABCD-EFGH" value={minecraft.inputCode} onChange={(event) => minecraft.setInputCode(event.target.value)} disabled={minecraft.loading} /></div>
          <div className="flex flex-wrap gap-2"><Button variant="outline" onClick={minecraft.confirm} disabled={minecraft.loading || !minecraft.inputCode}>Подтвердить</Button></div>
        </div>
      </Card>
    </div>
  );
}

export default React.memo(IntegrationsSettingsSection);
