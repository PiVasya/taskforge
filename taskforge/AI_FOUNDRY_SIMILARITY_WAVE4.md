# AI Foundry Similarity Wave 4

Что добавлено в этой волне:
- новый job type `assignment_similarity_review`
- новый stage `similarity_review`
- similarity review подключён в общий review pipeline после draft generation и после repair
- similarity review использует `referenceAssignments`, если они доступны в payload review-джобы
- repair loop теперь ждёт 4 review-стадии:
  - structural
  - pedagogy
  - similarity
  - runtime

Что делает similarity review:
- сравнивает текущий draft с reference assignments
- считает похожесть по title и description
- отдаёт findings, если задача выглядит слишком похожей на существующие примеры
- добавляет topReferences в resultJson review-джобы

Ограничения:
- используется лёгкое text-similarity сравнение
- batch-level duplicate review между draft items ещё не реализован
- cross-course persistent memory ещё не реализована
