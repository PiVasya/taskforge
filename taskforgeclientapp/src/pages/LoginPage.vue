<template>
  <AuthLayout title="Вход в систему">
    <form @submit.prevent="handleSubmit">
      <InputField id="email"
                  name="email"
                  label="Email"
                  type="email"
                  v-model="email"
                  autocomplete="email" />
      <PasswordField id="current-password"
                     name="password"
                     label="Пароль"
                     v-model="password"
                     autocomplete="current-password" />
      <div v-if="error" class="error-message">{{ error }}</div>
      <button type="submit" class="submit-button">Войти</button>
    </form>
    <template #footer>
      Нет аккаунта?
      <router-link to="/register">Зарегистрируйтесь</router-link>
    </template>
  </AuthLayout>
</template>

<script setup>
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import AuthLayout from '../components/AuthLayout.vue';
import InputField from '../components/InputField.vue';
import PasswordField from '../components/PasswordField.vue';
import { login } from '../services/authService.js';

const email = ref('');
const password = ref('');
const error = ref('');
const router = useRouter();

async function handleSubmit() {
  error.value = '';
  try {
    const response = await login(email.value, password.value);
    // сохраняем токен и email (для приветствия на dashboard)
    localStorage.setItem('token', response.token);
    localStorage.setItem('userEmail', email.value);
    router.push('/dashboard');
  } catch (err) {
    error.value = err.message || 'Не удалось войти.';
  }
}
</script>
