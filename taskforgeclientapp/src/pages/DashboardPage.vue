<template>
  <div class="auth-container">
    <h2 class="auth-title">Добро пожаловать{{ userEmail ? `, ${userEmail}` : '' }}!</h2>
    <p>Это тестовая страница. Здесь можно разместить основное содержимое приложения.</p>
    <button type="button" class="submit-button" @click="logout">Выйти</button>
  </div>
</template>

<script setup>
import { computed } from 'vue';
import { useRouter } from 'vue-router';

const router = useRouter();
const userEmail = computed(() => localStorage.getItem('userEmail') || '');

function logout() {
  localStorage.removeItem('token');
  localStorage.removeItem('userEmail');
  router.push('/login');
}
</script>
