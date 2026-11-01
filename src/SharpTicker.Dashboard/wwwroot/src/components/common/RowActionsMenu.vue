<script setup lang="ts">
import type { RowAction } from '@/utilities/rowActions'

defineProps<{
  actions: RowAction[]
  /** Accessible name of the trigger, for example "More actions for Foo". */
  label?: string
}>()

const emit = defineEmits<{ (e: 'select', key: string): void }>()
</script>

<template>
  <v-menu location="bottom end">
    <template #activator="{ props }">
      <button
        v-bind="props"
        type="button"
        class="row-actions-trigger"
        :aria-label="label ?? 'More actions'"
        title="More actions"
      >
        <v-icon size="18">mdi-dots-horizontal</v-icon>
      </button>
    </template>
    <v-list density="compact" class="row-actions-list">
      <template v-for="action in actions" :key="action.key">
        <v-divider v-if="action.danger" class="my-1" />
        <v-list-item
          :disabled="action.disabledReason !== undefined"
          :class="{ 'row-actions-danger': action.danger }"
          :prepend-icon="action.icon"
          :title="action.label"
          :subtitle="action.disabledReason"
          @click="emit('select', action.key)"
        />
      </template>
    </v-list>
  </v-menu>
</template>

<style scoped>
.row-actions-trigger {
  width: 32px;
  height: 32px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 8px;
  border: 1px solid rgba(255, 255, 255, 0.12);
  background: transparent;
  color: inherit;
  cursor: pointer;
}
.row-actions-trigger:hover,
.row-actions-trigger:focus-visible {
  background: rgba(255, 255, 255, 0.08);
}
.row-actions-trigger:active {
  transform: scale(0.94);
}
.row-actions-list {
  min-width: 220px;
}
.row-actions-danger {
  color: rgb(var(--v-theme-error));
}
:deep(.v-list-item-subtitle) {
  white-space: normal;
  opacity: 0.8;
}
</style>
