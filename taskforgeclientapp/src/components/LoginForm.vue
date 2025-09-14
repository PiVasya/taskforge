<script setup lang="ts">
import { useLoginForm } from '@/composables/useLoginForm'
const { form, errors, canSubmit, submitting, submit, validate } = useLoginForm()
</script>

<template>
  <form class="login" @submit.prevent="submit" @input="validate">
    <h2>Вход</h2>
    <label>
      Email
      <input v-model="form.email" type="email" autocomplete="email" />
      <span class="err" v-if="errors.email">{{ errors.email }}</span>
    </label>
    <label>
      Пароль
      <input v-model="form.password" type="password" autocomplete="current-password" />
      <span class="err" v-if="errors.password">{{ errors.password }}</span>
    </label>
    <button :disabled="!canSubmit">
      <span v-if="submitting">…</span>
      <span v-else>Войти</span>
    </button>
  </form>
</template>

<style scoped>
  /* можно переиспользовать стили из register.css */
</style>
