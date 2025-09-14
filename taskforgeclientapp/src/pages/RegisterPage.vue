<template>
  <AuthLayout title="Регистрация">
    <form @submit.prevent="handleSubmit">
      <InputField id="reg-email"
                  name="email"
                  label="Email"
                  type="email"
                  v-model="email"
                  autocomplete="email" />
      <PasswordField id="new-password"
                     name="password"
                     label="Пароль"
                     v-model="password"
                     autocomplete="new-password" />
      <PasswordField id="confirm-password"
                     name="confirmPassword"
                     label="Подтверждение пароля"
                     v-model="confirmPassword"
                     autocomplete="new-password" />
      <div v-if="error" class="error-message">{{ error }}</div>
      <button type="submit" class="submit-button">Зарегистрироваться</button>
    </form>
    <template #footer>
      Уже есть аккаунт?
      <router-link to="/login">Войти</router-link>
    </template>
  </AuthLayout>
</template>

<script setup>
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import AuthLayout from '../components/AuthLayout.vue';
import InputField from '../components/InputField.vue';
import PasswordField from '../components/PasswordField.vue';
import { register } from '../services/authService.js';

const email = ref('');
const password = ref('');
const confirmPassword = ref('');
const error = ref('');
const router = useRouter();

async function handleSubmit() {
  error.value = '';
  if (password.value !== confirmPassword.value) {
    error.value = 'Пароли не совпадают';
    return;
  }
  try {
    await register(email.value, password.value);
    router.push('/login');
  } catch (err) {
    error.value = err.message || 'Не удалось зарегистрироваться.';
  }
}
</script>
