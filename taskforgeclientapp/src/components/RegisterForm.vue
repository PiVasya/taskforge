<script setup lang="ts">
import { useRegisterForm } from '@/composables/useRegisterForm'

const {
  form, errors,
  canSubmit, submitting,
  submit, validate
} = useRegisterForm()
</script>

<template>
  <form class="register" @submit.prevent="submit" @input="validate">
    <h2>Create account</h2>

    <label>
      Email
      <input v-model="form.email" type="email" autocomplete="email" />
      <span class="err" v-if="errors.email">{{ errors.email }}</span>
    </label>

    <label>
      Username
      <input v-model="form.username" type="text" autocomplete="username" />
      <span class="err" v-if="errors.username">{{ errors.username }}</span>
    </label>

    <label>
      Password
      <input v-model="form.password" type="password" autocomplete="new-password" />
      <span class="err" v-if="errors.password">{{ errors.password }}</span>
    </label>

    <label>
      Confirm password
      <input v-model="form.confirm" type="password" autocomplete="new-password" />
      <span class="err" v-if="errors.confirm">{{ errors.confirm }}</span>
    </label>

    <button :disabled="!canSubmit">
      <span v-if="submitting">…</span>
      <span v-else>Register</span>
    </button>
  </form>
</template>

<style src="@/styles/register.css"></style>
