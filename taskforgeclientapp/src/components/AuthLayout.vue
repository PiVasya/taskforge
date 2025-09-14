<template>
  <div class="auth-container">
    <h2 class="auth-title">{{ title }}</h2>
    <slot />
    <div v-if="$slots.footer" class="footer-link">
      <slot name="footer" />
    </div>
  </div>
</template>

<script setup>
defineProps({
  title: {
    type: String,
    required: true
  }
});
</script>
