<template>
  <div class="form-group" style="position: relative;">
    <label :for="id">{{ label }}</label>
    <input :id="id"
           :name="name"
           :type="showPassword ? 'text' : 'password'"
           v-model="innerValue"
           :autocomplete="autocomplete"
           :required="required" />
    <button type="button"
            class="password-toggle"
            @click="togglePassword"
            :aria-label="showPassword ? 'Скрыть пароль' : 'Показать пароль'">
      {{ showPassword ? 'Скрыть' : 'Показать' }}
    </button>
    <div v-if="error" class="error-message">{{ error }}</div>
  </div>
</template>

<script setup>
import { ref, computed } from 'vue';

const props = defineProps({
  id: String,
  name: String,
  label: String,
  modelValue: String,
  autocomplete: { type: String, default: 'current-password' },
  required: { type: Boolean, default: true },
  error: String
});
const emit = defineEmits(['update:modelValue']);

const showPassword = ref(false);
const togglePassword = () => {
  showPassword.value = !showPassword.value;
};

const innerValue = computed({
  get: () => props.modelValue,
  set: val => emit('update:modelValue', val)
});
</script>
