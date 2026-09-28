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
				<IndicatorThresholdEditor :item="item" />
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
				<el-form-item v-if="item.evidenceType === 'protocol' || ['industrial-protocol', 'transfer-protocol'].includes(item.metricId ?? '')" label="待评价标准协议集合（不是已支持清单）">
					<el-checkbox-group v-model="item.protocols" class="protocol-options">
						<el-checkbox v-for="protocol in availableProtocols" :key="protocol" :value="protocol">{{ protocol }}</el-checkbox>
					</el-checkbox-group>
					<div class="rule-note">协议别名归一后去重。勾选仅定义本轮评价范围，不作为通信成功证据；修改后保存配置才会影响新任务快照。</div>
				</el-form-item>
				<el-form-item v-if="item.evidenceType === 'file'" label="测试文件">
					<div class="metric-files">
						<el-upload drag multiple :show-file-list="false" :http-request="upload" :disabled="uploading"
							accept=".nc,.ncg,.txt,.xml,.prg">
							<el-icon class="el-icon--upload"><UploadFilled /></el-icon>
							<div>点击或拖拽上传测试文件</div>
							<template #tip><div class="el-upload__tip">支持 .nc、.ncg、.txt、.xml、.prg，单个文件不超过 200 MiB（209715200 字节）。上传仅存至网关，写入设备需任务单独授权。</div></template>
						</el-upload>
						<el-button v-if="uploading" link type="warning" @click="cancelUploads">取消当前上传</el-button>
						<div v-for="(file, index) in item.files" :key="file.id || `${file.name}-${index}`" class="metric-file">
							<span>{{ file.name }} <small>({{ file.size }}){{ file.id ? '' : ' · 未上传' }}</small></span>
							<el-button type="danger" link @click="item.files.splice(index, 1)">移除</el-button>
						</div>
					</div>
				</el-form-item>
				<AutomationRuleEditor :item="item" />
				<el-collapse class="scoring-editor">
					<el-collapse-item title="历史评分文字（JSON，仅展示，不参与自动测试）" name="scoring">
						<el-input v-model="scoringText" type="textarea" :rows="7" aria-label="历史评分文字 JSON" />
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
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { ElMessage, type UploadRequestOptions } from "element-plus";
import { UploadFilled } from "@element-plus/icons-vue";
import { evaluationApi, MAX_EVALUATION_ATTACHMENT_BYTES, type EvaluationItem } from "@/api/evaluation";
import { clone, levelLabels, protocolOptions, type MetricEdit } from "./metricTree";
import AutomationRuleEditor from "./automationRuleEditor.vue";
import IndicatorThresholdEditor from "./indicatorThresholdEditor.vue";
import { normalizeProtocol, validate } from "./automationRules";

const props = defineProps<{ target: MetricEdit | null }>();
const emit = defineEmits<{ close: []; save: [edit: MetricEdit] }>();
const draft = ref<MetricEdit | null>(null);
const scoringText = ref("{}");
const uploadCount = ref(0);
const uploading = computed(() => uploadCount.value > 0);
const item = computed(() => draft.value?.level === "item" ? draft.value.node as EvaluationItem : null);
const title = computed(() => draft.value ? `${draft.value.isNew ? '添加' : '编辑'}${levelLabels[draft.value.level]}` : "编辑指标");
const availableProtocols = computed(() => [...new Set([...protocolOptions, ...(item.value?.protocols ?? [])])]);
const uploads = new Set<AbortController>();
function cancelUploads() { for (const upload of uploads) upload.abort(); }
onBeforeUnmount(cancelUploads);
watch(() => props.target, target => {
	cancelUploads();
	draft.value = target ? clone(target) : null;
	if (item.value) item.value.protocols = [...new Set(item.value.protocols.map(normalizeProtocol))];
	scoringText.value = JSON.stringify(item.value?.scoring ?? {}, null, 2);
}, { immediate: true });
async function upload(options: UploadRequestOptions) {
	const currentItem = item.value;
	if (!currentItem) return;
	if (!/\.(nc|ncg|txt|xml|prg)$/i.test(options.file.name) || options.file.size <= 0 || options.file.size > MAX_EVALUATION_ATTACHMENT_BYTES) {
		ElMessage.warning("请选择支持的测试文件，单个文件应大于 0 且不超过 200 MiB");
		return;
	}
	const controller = new AbortController();
	uploads.add(controller);
	uploadCount.value += 1;
	try {
		const uploaded = await evaluationApi.uploadAttachment(options.file, controller.signal);
		if (controller.signal.aborted || item.value !== currentItem) return;
		currentItem.files.push(uploaded);
		ElMessage.success("测试文件已上传");
	} catch {
		if (!controller.signal.aborted) ElMessage.error("测试文件上传失败，请重试");
	} finally {
		uploads.delete(controller);
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
		const ruleError = validate(item.value);
		if (ruleError) return void ElMessage.warning(ruleError);
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
.rule-note { color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.7; margin-top: 8px; }
@media (max-width: 600px) { .metric-form-row { grid-template-columns: 1fr; gap: 0; } .protocol-options { grid-template-columns: repeat(2, 1fr); } }
</style>
