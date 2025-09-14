<template>
  <div class="form-group">
    <label :for="id">{{ label }}</label>
    <input :id="id"
           :name="name"
           :type="type"
           v-model="innerValue"
           :autocomplete="autocomplete"
           :required="required" />
    <div v-if="error" class="error-message">{{ error }}</div>
  </div>
</template>

<script setup>
import { computed } from 'vue';

const props = defineProps({
  id: String,
  name: String,
  label: String,
  type: { type: String, default: 'text' },
  modelValue: String,
  autocomplete: String,
  required: { type: Boolean, default: true },
  error: String
});
const emit = defineEmits(['update:modelValue']);

const innerValue = computed({
  get: () => props.modelValue,
  set: val => emit('update:modelValue', val)
});
</script>
