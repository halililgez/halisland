<script setup lang="ts">
import { computed } from "vue";
import type { ClaudeInfo, ClaudeState } from "../composables/useClaudeStatus";

const props = defineProps<{
  state: ClaudeState;
  info: ClaudeInfo;
  label: string;
  detail: string;
  elapsed: string;
}>();

const emit = defineEmits<{ focus: [] }>();

const idle = computed(() => props.state === "idle" && !props.info.prompt);
const filesTitle = computed(() => props.info.files.join("\n"));
const others = computed(() => Math.max(0, props.info.sessions - 1));
</script>

<template>
  <div v-if="idle" class="idle">
    <div class="big">{{ label }}</div>
    <div class="sub">Claude Code bekleniyor</div>
  </div>

  <div v-else class="claude">
    <div class="head">
      <span class="big">{{ label }}</span>
      <span v-if="info.project" class="project" :title="info.project">{{ info.project }}</span>
      <span v-if="others" class="others" :title="`${others} oturum daha açık`">+{{ others }}</span>
    </div>

    <div class="detail">
      <span class="what" :title="detail">{{ detail }}</span>
      <span v-if="state === 'working' || state === 'waiting'" class="time mono">{{ elapsed }}</span>
    </div>

    <div v-if="info.prompt" class="prompt" :title="info.prompt">{{ info.prompt }}</div>

    <div class="actions">
      <span v-if="info.files.length" class="chip files" :title="filesTitle">
        {{ info.files.length }} dosya
      </span>
      <button v-if="info.canFocus" class="chip go" title="Claude'un çalıştığı pencereyi öne getir" @click="emit('focus')">
        Terminale dön
        <svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M7 17 17 7M9 7h8v8" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" /></svg>
      </button>
    </div>
  </div>
</template>

<style scoped>
.claude {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.head {
  display: flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
}

.big {
  font-size: 18px;
  font-weight: 600;
  line-height: 1.1;
  white-space: nowrap;
}

.idle .big {
  font-size: 22px;
}

.sub {
  margin-top: 4px;
  font-size: 11px;
  color: var(--text-dim);
}

.project {
  min-width: 0;
  font-size: 10px;
  color: var(--text-dim);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.others {
  flex: none;
  margin-left: auto;
  padding: 0 5px;
  border-radius: 999px;
  font-size: 9px;
  line-height: 15px;
  background: rgba(255, 255, 255, 0.1);
}

.detail {
  display: flex;
  gap: 6px;
  font-size: 11px;
  color: var(--text-dim);
  min-width: 0;
}

.what {
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.time {
  flex: none;
  margin-left: auto;
}

.prompt {
  font-size: 11px;
  line-height: 1.35;
  color: rgba(244, 244, 246, 0.78);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  border-left: 2px solid color-mix(in srgb, var(--glow, #fff) 60%, transparent);
  padding-left: 6px;
}

.actions {
  display: flex;
  gap: 6px;
  margin-top: 2px;
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 10px;
  white-space: nowrap;
  background: rgba(255, 255, 255, 0.08);
}

.files {
  cursor: default;
}

.go:hover {
  background: rgba(255, 255, 255, 0.18);
}

.go svg {
  width: 10px;
  height: 10px;
}
</style>
