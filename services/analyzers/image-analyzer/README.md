# Image Analyzer (Neural Image Similarity)

Отдельный контейнер для **сравнения изображений** (image‑задания), чтобы не раздувать основной `taskforge` API.

## Идея

Текущий сравниватель в API использует простой perceptual hash (dHash). Он быстрый и удобный, но часто «плывёт» на:

- мелких смещениях/антиалиасинге,
- другой толщине линий,
- небольших изменениях палитры,
- генерации через разные языки/библиотеки.

Этот сервис делает сравнение через **нейронную модель (OpenCLIP)** и возвращает метрики, которые можно использовать в правилах прохождения.

## Эндпоинты

### `GET /health`
Простой healthcheck.

### `POST /compare`
Сравнение двух картинок.

**multipart/form-data**:
- `expected`: файл (эталон)
- `actual`: файл (результат пользователя)

**query params (опционально)**:
- `threshold` (float, по умолчанию `0.90`) — порог прохождения для `clip_similarity`.

Ответ:

```json
{
  "passed": true,
  "threshold": 0.9,
  "clip_similarity": 0.9321,
  "phash_similarity": 0.875,
  "combined_similarity": 0.915,
  "model": "ViT-L-14 / laion2b_s32b_b82k"
}
```

## Запуск локально

```bash
docker build -t taskforge-image-analyzer:local ./image-analyzer
docker run --rm -p 8090:8080 taskforge-image-analyzer:local
```

Проверка:

```bash
curl -s http://localhost:8090/health
```

## Настройки через ENV

- `MODEL_NAME` (default: `ViT-L-14`)
- `MODEL_PRETRAINED` (default: `laion2b_s32b_b82k`)
- `CLIP_WEIGHT` (default: `0.85`) — вес CLIP в `combined_similarity`
- `PHASH_WEIGHT` (default: `0.15`) — вес pHash в `combined_similarity`
- `OPENCLIP_CACHE_DIR` (default: `/models/openclip`) — куда скачивать веса

> Примечание: сервис специально вынесен в отдельный контейнер, чтобы основной API не «толстел» из‑за PyTorch/ML зависимостей.
