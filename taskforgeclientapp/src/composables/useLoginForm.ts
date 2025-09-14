import { ref, computed } from 'vue'

export function useLoginForm() {
  const form = ref({ email: '', password: '' })
  const errors = ref({ email: null, password: null } as Record<string, string | null>)

  const validate = () => {
    errors.value.email = /^[\w.+-]+@\w+\.\w{2,}$/.test(form.value.email) ? null : 'Неверный email'
    errors.value.password = form.value.password.length >= 6 ? null : 'Пароль ≥ 6 символов'
    return Object.values(errors.value).every(e => e === null)
  }

  const submitting = ref(false)
  const submit = async () => {
    if (!validate()) return
    submitting.value = true
    try {
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(form.value)
      })
      const data = await res.json()
      if (!res.ok) {
        alert('❌ ' + (data.message ?? 'Ошибка входа'))
        return
      }
      // сохраните токен/куки, если сервер выдаёт их в ответ
      alert('✅ ' + data.message)
    } finally {
      submitting.value = false
    }
  }

  const canSubmit = computed(() => !submitting.value && Object.values(form.value).every(Boolean))
  return { form, errors, canSubmit, submitting, submit, validate }
}
