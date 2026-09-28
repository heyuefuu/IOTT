<template>
	<el-dialog :model-value="Boolean(target)" :title="title" width="min(760px, 94vw)"
		:close-on-click-modal="false" @close="emit('close')">
		<el-form v-if="draft" label-position="top" @submit.prevent="submit">
			<div class="metric-form-row">
				<el-form-item :label="draft.level === 'item' ? '项目名称' : '分类名称'" required>
					<el-input v-model="draft.node.name" maxlength="200" />
				</el-form-item>
				<el-form-item label="权重 (%)" required>
					<el-input-number v-model="draft.node.weight" :min="0" :max="100" :precision="1" :step="0.1" />
				</el-form-item>
			</div>
			<template v-if="item">
				<el-form-item label="项目描述"><el-input v-model="item.desc" type="textarea" :rows="3" /></el-form-item>
				<el-form-item label="评价方法与评分标准"><el-input v-model="item.method" type="textarea" :rows="3" /></el-form-item>
				<el-form-item label="测试依据类型">
					<el-radio-group v-model="item.evidenceType">
						<el-radio-button value="standard">标准规范</el-radio-button>
						<el-radio-button value="protocol">协议勾选</el-radio-button>
						<el-radio-button value="file">文件上传</el-radio-button>
					</el-radio-group>
				</el-form-item>
				<el-form-item label="标准 / 规范名称"><el-input v-model="item.standards" placeholder="如 GB/T 15579.1-2013" /></el-form-item>
				<el-form-item v-if="item.evidenceType === 'protocol'" label="勾选测试协议">
					<el-checkbox-group v-model="item.protocols" class="protocol-options">
						<el-checkbox v-for="protocol in protocolOptions" :key="protocol" :value="protocol">{{ protocol }}</el-checkbox>
					</el-checkbox-group>
				</el-form-item>
				<el-form-item v-if="item.evidenceType === 'file'" label="测试文件">
					<div class="metric-files">
						<el-upload drag multiple :show-file-list="false" :http-request="upload" :disabled="uploading"
							accept=".nc,.ncg,.txt,.xml,.prg">
							<el-icon class="el-icon--upload"><UploadFilled /></el-icon>
							<div>点击或拖拽上传测试文件</div>
							<template #tip><div class="el-upload__tip">支持 .nc、.ncg、.txt、.xml、.prg，单个文件不超过 100 MB</div></template>
						</el-upload>
						<div v-for="(file, index) in item.files" :key="file.id || `${file.name}-${index}`" class="metric-file">
							<span>{{ file.name }} <small>({{ file.size }}){{ file.id ? '' : ' · 未上传' }}</small></span>
							<el-button type="danger" link @click="item.files.splice(index, 1)">移除</el-button>
						</div>
					</div>
				</el-form-item>
				<el-collapse class="scoring-editor">
					<el-collapse-item title="程序识别评分规则（JSON）" name="scoring">
						<el-input v-model="scoringText" type="textarea" :rows="7" aria-label="评分规则 JSON" />
					</el-collapse-item>
				</el-collapse>
			</template>
		</el-form>
		<template #footer>
			<el-button @click="emit('close')">取消</el-button>
			<el-button type="primary" :disabled="uploading" @click="submit">保存</el-button>
		</template>
	</el-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { ElMessage, type UploadRequestOptions } from "element-plus";
import { UploadFilled } from "@element-plus/icons-vue";
import { evaluationApi, type EvaluationItem } from "@/api/evaluation";
import { clone, levelLabels, protocolOptions, type MetricEdit } from "./metricTree";

const props = defineProps<{ target: MetricEdit | null }>();
const emit = defineEmits<{ close: []; save: [edit: MetricEdit] }>();
const draft = ref<MetricEdit | null>(null);
const scoringText = ref("{}");
const uploadCount = ref(0);
const uploading = computed(() => uploadCount.value > 0);
const item = computed(() => draft.value?.level === "item" ? draft.value.node as EvaluationItem : null);
const title = computed(() => draft.value ? `${draft.value.isNew ? '添加' : '编辑'}${levelLabels[draft.value.level]}` : "编辑指标");
watch(() => props.target, target => {
	draft.value = target ? clone(target) : null;
	scoringText.value = JSON.stringify(item.value?.scoring ?? {}, null, 2);
});
async function upload(options: UploadRequestOptions) {
	const currentItem = item.value;
	if (!currentItem) return;
	if (!/\.(nc|ncg|txt|xml|prg)$/i.test(options.file.name) || options.file.size > 100 * 1024 * 1024) {
		ElMessage.warning("请选择支持的测试文件，单个文件不超过 100 MB");
		return;
	}
	uploadCount.value += 1;
	try {
		const uploaded = await evaluationApi.uploadAttachment(options.file);
		currentItem.files.push(uploaded);
		ElMessage.success("测试文件已上传");
	} catch {
		ElMessage.error("测试文件上传失败，请重试");
	} finally {
		uploadCount.value -= 1;
	}
}

function submit() {
	if (!draft.value || uploading.value) return;
	if (!draft.value.node.name.trim()) return void ElMessage.warning("请填写名称");
	if (!Number.isFinite(draft.value.node.weight) || draft.value.node.weight < 0 || draft.value.node.weight > 100) {
		return void ElMessage.warning("权重应为 0 到 100 之间的数值");
	}
	if (item.value) {
		try {
			const scoring: unknown = JSON.parse(scoringText.value);
			if (!scoring || Array.isArray(scoring) || typeof scoring !== "object" || Object.values(scoring).some(value => typeof value !== "string")) throw new Error("Invalid scoring rules");
			item.value.scoring = scoring as Record<string, string>;
		} catch {
			return void ElMessage.warning("评分规则应为 JSON 对象，规则内容请使用文本");
		}
	}
	draft.value.node.name = draft.value.node.name.trim();
	emit("save", clone(draft.value));
}
</script>

<style scoped lang="scss">
.metric-form-row { display: grid; grid-template-columns: 1fr 180px; gap: 20px; }
.protocol-options { display: grid; grid-template-columns: repeat(3, 1fr); width: 100%; }
.metric-files { width: 100%; }
.metric-file { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 8px 0; overflow-wrap: anywhere; }
.metric-file small { color: var(--el-text-color-secondary); }
.scoring-editor { margin-top: 12px; }
@media (max-width: 600px) { .metric-form-row { grid-template-columns: 1fr; gap: 0; } .protocol-options { grid-template-columns: repeat(2, 1fr); } }
</style>
