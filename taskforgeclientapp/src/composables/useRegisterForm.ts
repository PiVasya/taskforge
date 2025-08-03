import { ref, computed } from 'vue'

export interface RegisterFields {
    email: string
    username: string
    password: string
    confirm: string
}

export function useRegisterForm() {
    // reactive state
    const form = ref<RegisterFields>({
        email: '',
        username: '',
        password: '',
        confirm: ''
    })

    // simple validators
    const errors = ref<Record<keyof RegisterFields, string | null>>({
        email: null,
        username: null,
        password: null,
        confirm: null
    })

    const isValidEmail = (val: string) =>
        /^[\w.+-]+@\w+\.\w{2,}$/.test(val)

    const validate = () => {
        errors.value.email = isValidEmail(form.value.email)
            ? null
            : 'Invalid e-mail'
        errors.value.username = form.value.username.length >= 3
            ? null
            : 'Username ≥ 3 chars'
        errors.value.password = form.value.password.length >= 6
            ? null
            : 'Password ≥ 6 chars'
        errors.value.confirm =
            form.value.confirm === form.value.password
                ? null
                : 'Passwords differ'
        // вернём true, если нет ошибок
        return Object.values(errors.value).every(e => e === null)
    }

    // submit-handler (здесь пока заглушка)
    const submitting = ref(false)
    const submit = async () => {
        if (!validate()) return
        submitting.value = true
        try {
            // TODO: заменить на реальный fetch/axios
            await new Promise(r => setTimeout(r, 800))
            alert('✅ Registered (mock)')
        } finally {
            submitting.value = false
        }
    }

    // можно вычислить флаг готовности кнопки
    const canSubmit = computed(
        () => !submitting.value && Object.values(form.value).every(Boolean)
    )

    return { form, errors, canSubmit, submitting, submit, validate }
}
