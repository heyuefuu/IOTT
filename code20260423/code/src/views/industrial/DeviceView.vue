<template>
    <div class="device-view">
        <h2 class="page-title">设备管理</h2>

        <div class="device-layout">
            <!-- 左侧树 -->
            <el-card class="tree-panel" shadow="never">
                <template #header>
                    <div class="tree-title">设备</div>
                </template>
                <el-input v-model="treeKeyword" placeholder="按设备名称或点位搜索" class="tree-search" />
                <el-tree ref="treeRef" :data="deviceTree" node-key="id" :default-expanded-keys="['all']"
                    :expand-on-click-node="false" :filter-node-method="filterTreeNode"
                    @node-click="handleTreeNodeClick" />
            </el-card>

            <!-- 右侧内容 -->
            <div>
                <!-- 操作栏 -->
                <div class="action-bar">
                    <el-button :loading="devicesLoading" @click="loadDevices">
                        刷新列表
                    </el-button>
                    <el-button type="primary" @click="openAddDeviceDialog">
                        <el-icon>
                            <Plus />
                        </el-icon>
                        新增设备
                    </el-button>
                    <el-button @click="openBatchAddDialog">
                        <el-icon>
                            <DocumentAdd />
                        </el-icon>
                        批量新增
                    </el-button>
                    <el-button @click="exportDeviceTemplate">
                        <el-icon>
                            <Download />
                        </el-icon>
                        导出模板
                    </el-button>
                    <el-button @click="importDevices">
                        <el-icon>
                            <Upload />
                        </el-icon>
                        导入设备
                    </el-button>
                    <el-button :loading="syncingUpstream" @click="syncUpstreamDevices">
                        <el-icon>
                            <Connection />
                        </el-icon>
                        同步到采集服务
                    </el-button>
                    <el-button @click="openNclinkDialog">
                        <el-icon>
                            <Cpu />
                        </el-icon>
                        NC-Link 诊断
                    </el-button>
                    <el-input v-model="searchKeyword" placeholder="搜索设备名称/编号/IP/协议" style="width: 300px; margin-left: auto"
                        prefix-icon="Search" />
                    <input ref="deviceImportInputRef" type="file" accept=".csv,text/csv" hidden
                        @change="handleDeviceImportFile" />
                </div>

                <!-- 设备卡片列表（数据来自 MachineConnectionApi → Industrial IoT） -->
                <div v-if="devicesError" class="devices-api-hint devices-api-hint--error">
                    {{ devicesError }}。请确认：MachineConnectionApi 已启动（与
                    <code>.env</code>
                    中端口一致，常见
                    <strong>{{ machineConnectionPort }}</strong>
                    ）；IndustrialIoT.Host 已启动（常见 Swagger
                    <strong>{{ industrialIotPort }}</strong>
                    ）；且网关的
                    <code>IndustrialIoT:BaseUrl</code>
                    指向该 IoT 根地址。开发环境需
                    <code>npm run dev</code>
                    以启用 Vite 代理（
                    <code>VITE_MACHINE_CONNECTION_PROXY_TARGET</code>
                    ）。
                </div>
                <div v-else-if="!devicesLoading && devicesFiltered.length === 0" class="devices-api-hint">
                    暂无设备。
                </div>
                <div v-loading="devicesLoading" class="device-cards">
                    <el-card v-for="device in filteredDevices" :key="device.id" :body-style="{ padding: '20px' }"
                        class="device-card">
                        <div class="card-header">
                            <div class="device-info">
                                <el-tag size="small" :type="isCollectingDevice(device.id) ? 'success' : 'info'">
                                    {{ isCollectingDevice(device.id) ? "采集中" : "停止" }}
                                </el-tag>
                                <h3 class="device-name">{{ device.name }}</h3>
                                <p class="device-code">设备编号：{{ device.code || "-" }} | {{ device.model }}</p>
                            </div>
                            <div class="device-status">
                                <el-tag
                                    :type="device.status === 'online' ? 'success' : device.status === 'offline' ? 'warning' : 'danger'"
                                    :effect="'dark'">
                                    {{
                                        device.status === "online"
                                            ? "在线"
                                            : device.status === "offline"
                                                ? "离线"
                                                : "异常"
                                    }}
                                </el-tag>
                            </div>
                        </div>

                        <div class="card-body">
                            <div class="info-row">
                                <el-descriptions :column="2" :size="'small'">
                                    <el-descriptions-item label="协议类型">{{ device.protocol }}</el-descriptions-item>
                                    <el-descriptions-item label="IP地址">{{ device.ip }}</el-descriptions-item>
                                    <el-descriptions-item label="端口">{{ device.port }}</el-descriptions-item>
                                    <el-descriptions-item label="品牌">{{ device.brand }}</el-descriptions-item>
                                    <el-descriptions-item label="最后通讯" :span="2">{{ device.lastCommTime
                                    }}</el-descriptions-item>
                                </el-descriptions>
                            </div>
                        </div>

                        <div class="card-footer">
                            <div class="card-footer-row">
                                <el-button type="success" link size="small" @click="testConnection(device.id)">
                                    连接测试
                                </el-button>
                                <el-button type="primary" link size="small" @click="openPointDialog(device)">
                                    点位
                                </el-button>
                                <el-button type="success" link size="small" @click="runCollection(device.id)">
                                    开始采集
                                </el-button>
                                <el-button type="warning" link size="small" @click="stopCollection(device.id)">
                                    停止采集
                                </el-button>
                                <el-button type="primary" link size="small" @click="openCollectionHistoryDialog(device.id)">
                                    历史采集记录
                                </el-button>
                                <el-button type="success" link size="small" @click="openTransferDialog(device)">
                                    传输
                                </el-button>
                            </div>
                            <div class="card-footer-row">
                                <el-button type="primary" link size="small" @click="viewDeviceDetail(device)">
                                    详情
                                </el-button>
                                <el-button type="warning" link size="small" @click="editDevice(device)">
                                    编辑
                                </el-button>
                                <el-button type="danger" link size="small" @click="deleteDevice(device.id)">
                                    删除
                                </el-button>
                            </div>
                        </div>
                    </el-card>
                </div>

                <!-- 分页 -->
                <div class="pagination">
                    <el-pagination v-model:current-page="currentPage" v-model:page-size="pageSize"
                        :page-sizes="[10, 20, 50, 100]" layout="total, sizes, prev, pager, next, jumper"
                        :total="totalDevices" @size-change="handleSizeChange" @current-change="handleCurrentChange" />
                </div>
            </div>
        </div>

        <!-- 新增/编辑设备弹窗 -->
        <el-dialog v-model="dialogVisible" :title="dialogTitle" width="800px">
            <el-form :model="deviceForm" label-width="120px">
                <el-form-item label="设备名称" prop="name" required>
                    <el-input v-model="deviceForm.name" placeholder="请输入设备名称" />
                </el-form-item>
                <el-form-item label="设备编号" prop="code" required>
                    <el-input v-model="deviceForm.code" placeholder="请输入设备编号" />
                </el-form-item>
                <el-form-item label="设备类型" prop="deviceType" required>
                    <el-select v-model="deviceForm.deviceType" placeholder="请选择设备类型" style="width: 100%">
                        <el-option label="CNC" value="CNC" />
                        <el-option label="PLC" value="PLC" />
                        <el-option label="Robot" value="Robot" />
                    </el-select>
                </el-form-item>
                <el-form-item label="品牌" prop="brand" required>
                    <el-select v-model="deviceForm.brand" placeholder="请选择品牌" style="width: 100%">
                        <el-option label="马扎克（Mazak）" value="马扎克（Mazak）" />
                        <el-option label="哈斯（Haas）" value="哈斯（Haas）" />
                        <el-option label="兄弟（Brother）" value="兄弟（Brother）" />
                        <el-option label="牧野（Makino）" value="牧野（Makino）" />
                        <el-option label="北京精雕" value="北京精雕" />
                        <el-option label="华中数控" value="华中数控" />
                        <el-option label="广州数控" value="广州数控" />
                        <el-option label="西门子" value="西门子" />
                        <el-option label="法那科" value="法那科" />
                        <el-option label="海德汉" value="海德汉" />
                    </el-select>
                </el-form-item>
                <el-form-item label="型号" prop="model" required>
                    <el-input v-model="deviceForm.model" placeholder="如 0i-MF" />
                </el-form-item>

                <el-divider content-position="center">通信配置（协议 / 主机 / 端口）</el-divider>

                <el-form-item label="协议" prop="protocol" required>
                    <el-select v-model="deviceForm.protocol" placeholder="请选择协议类型" style="width: 100%">
                        <el-option label="Profibus" value="Profibus" />
                        <el-option label="Modbus TCP" value="ModbusTCP" />
                        <el-option label="NC-link" value="NCLink" />
                        <el-option label="NC-Link API Server (华中)" value="NCLinkApi" />
                        <el-option label="广数 (GSK WebServer)" value="GskWebServer" />
                        <el-option label="FANUC FOCAS" value="FOCAS" />
                        <el-option label="欧姆龙 FINS" value="FINS" />
                        <el-option label="松下 Mewtocol" value="Mewtocol" />
                        <el-option label="OPC UA" value="OpcUa" />
                    </el-select>
                </el-form-item>
                <el-form-item label="主机" prop="ip" required>
                    <el-input v-model="deviceForm.ip" placeholder="如 127.0.0.1" />
                </el-form-item>
                <el-form-item label="端口" prop="port">
                    <el-input v-model.number="deviceForm.port" placeholder="如 FOCAS 常用 8193" />
                </el-form-item>
                <el-form-item label="连接超时 ms" prop="connectTimeoutMs">
                    <el-input v-model.number="deviceForm.connectTimeoutMs" placeholder="connectTimeoutMs，默认 10000" />
                </el-form-item>
                <el-form-item label="读取超时 ms" prop="readTimeoutMs">
                    <el-input v-model.number="deviceForm.readTimeoutMs" placeholder="readTimeoutMs，默认 5000" />
                </el-form-item>

                <template v-if="deviceForm.deviceType === 'CNC'">
                    <el-divider content-position="center">程序文件传输配置</el-divider>
                    <el-form-item label="传输协议" prop="transferProtocol">
                        <el-select v-model="deviceForm.transferProtocol" placeholder="请选择文件传输协议" style="width: 100%">
                            <el-option label="不单独配置（使用主协议）" value="" />
                            <el-option label="FTP" value="FTP" />
                            <el-option label="SMB" value="SMB" />
                            <el-option label="NFS" value="NFS" />
                        </el-select>
                    </el-form-item>
                    <template v-if="deviceForm.transferProtocol">
                        <el-form-item label="传输主机" prop="transferHost" required>
                            <el-input v-model="deviceForm.transferHost" placeholder="如 192.168.1.20" />
                        </el-form-item>
                        <el-form-item label="传输端口" prop="transferPort" required>
                            <el-input v-model.number="deviceForm.transferPort" placeholder="FTP 默认 21，SMB 默认 445" />
                        </el-form-item>
                        <el-form-item v-if="deviceForm.transferProtocol === 'SMB'" label="共享名" prop="transferShareName" required>
                            <el-input v-model="deviceForm.transferShareName" placeholder="如 NC_PROGRAM" />
                        </el-form-item>
                        <el-form-item label="传输账号" prop="transferUsername">
                            <el-input v-model="deviceForm.transferUsername" placeholder="可留空使用匿名/来宾" />
                        </el-form-item>
                        <el-form-item label="传输密码" prop="transferPassword">
                            <el-input v-model="deviceForm.transferPassword" type="password" show-password />
                        </el-form-item>
                    </template>
                </template>

                <el-divider content-position="center">扩展属性</el-divider>

                <template v-if="deviceForm.protocol === 'FOCAS'">
                    <el-form-item label="轴标签" prop="axisLabels">
                        <el-input v-model="deviceForm.axisLabels" placeholder="如 X,Y,Z,A,B,C（发那科 FOCAS）" />
                    </el-form-item>
                </template>

                <template v-else-if="deviceForm.protocol === 'OpcUa'">
                    <el-form-item label="账号" prop="username">
                        <el-input v-model="deviceForm.username" placeholder="如 OpcUaClient" />
                    </el-form-item>
                    <el-form-item label="密码" prop="password">
                        <el-input v-model="deviceForm.password" type="password" show-password
                            placeholder="如 OpcUaClient" />
                    </el-form-item>
                    <el-form-item label="EndpointUrl" prop="endpointUrl">
                        <el-input v-model="deviceForm.endpointUrl" placeholder="留空将自动生成：opc.tcp://主机:端口" />
                    </el-form-item>
                    <el-form-item label="使用安全连接" prop="useSecurity">
                        <el-select v-model="deviceForm.useSecurity" placeholder="是否启用 OPC UA 安全" style="width: 100%">
                            <el-option label="否" value="false" />
                            <el-option label="是" value="true" />
                        </el-select>
                    </el-form-item>
                    <el-form-item label="自动接受不受信任证书" prop="autoAcceptUntrustedCerts">
                        <el-select v-model="deviceForm.autoAcceptUntrustedCerts" placeholder="是否信任未授信证书"
                            style="width: 100%">
                            <el-option label="是" value="true" />
                            <el-option label="否" value="false" />
                        </el-select>
                    </el-form-item>
                    <el-form-item label="拒绝SHA1证书" prop="rejectSHA1SignedCertificates">
                        <el-select v-model="deviceForm.rejectSHA1SignedCertificates" style="width: 100%">
                            <el-option label="否" value="false" />
                            <el-option label="是" value="true" />
                        </el-select>
                    </el-form-item>
                    <el-form-item label="抑制Nonce校验错误" prop="suppressNonceValidationErrors">
                        <el-select v-model="deviceForm.suppressNonceValidationErrors" style="width: 100%">
                            <el-option label="是" value="true" />
                            <el-option label="否" value="false" />
                        </el-select>
                    </el-form-item>

                </template>

                <template v-else-if="deviceForm.protocol === 'NCLink'">
                    <el-form-item label="设备唯一标识" prop="deviceGuid" required>
                        <el-input v-model="deviceForm.deviceGuid" placeholder="NC-Link 设备 GUID，须与现场一致" />
                    </el-form-item>
                    <el-form-item label="品牌（扩展）" prop="nclinkBrand">
                        <el-input v-model="deviceForm.nclinkBrand" placeholder="建议填 华中数控，用于统一标签映射" />
                    </el-form-item>
                    <el-form-item label="MQTT 代理地址" prop="mqttBrokerHost">
                        <el-input v-model="deviceForm.mqttBrokerHost" placeholder="可选；与主机相同时可不填" />
                    </el-form-item>
                    <el-form-item label="MQTT 代理端口" prop="mqttBrokerPort">
                        <el-input v-model="deviceForm.mqttBrokerPort" placeholder="可选；如 1883" />
                    </el-form-item>
                    <el-form-item label="MQTT 用户名" prop="mqttUsername">
                        <el-input v-model="deviceForm.mqttUsername" placeholder="可选" />
                    </el-form-item>
                    <el-form-item label="MQTT 密码" prop="mqttPassword">
                        <el-input v-model="deviceForm.mqttPassword" type="password" show-password placeholder="可选" />
                    </el-form-item>
                </template>

                <template v-else-if="deviceForm.protocol === 'NCLinkApi'">
                    <el-form-item label="DeviceId（机床 SN 码）" prop="ncLinkApiDeviceId" required>
                        <el-input v-model="deviceForm.ncLinkApiDeviceId" placeholder="如 1AFFFD1E7F36CAD，与机床 nclink.cfg 一致" />
                    </el-form-item>
                    <el-form-item label="ApiBaseUrl（可选）" prop="ncLinkApiBaseUrl">
                        <el-input v-model="deviceForm.ncLinkApiBaseUrl" placeholder="如 http://127.0.0.1:19001；留空则用上方主机+端口拼" />
                    </el-form-item>
                </template>

                <template v-else-if="deviceForm.protocol === 'GskWebServer'">
                    <el-form-item label="DeviceSn（设备序列号）" prop="gskDeviceSn" required>
                        <el-input v-model="deviceForm.gskDeviceSn" placeholder="如 cnc，对应机床 swagger 路径 /api/v1/{DeviceSn}" />
                    </el-form-item>
                    <el-form-item label="协议方案 Scheme" prop="gskScheme">
                        <el-select v-model="deviceForm.gskScheme" placeholder="默认 http" style="width: 100%">
                            <el-option label="http" value="http" />
                            <el-option label="https" value="https" />
                        </el-select>
                    </el-form-item>
                    <el-form-item label="管理端 BaseUrl（可选）" prop="gskManagementBaseUrl">
                        <el-input v-model="deviceForm.gskManagementBaseUrl" placeholder="如 http://主机:3000；留空自动用上方主机+3000" />
                    </el-form-item>
                    <el-form-item label="车间认证 Token（可选）" prop="gskWorkshopAuthToken">
                        <el-input v-model="deviceForm.gskWorkshopAuthToken" placeholder="X-Authorization-Token，访问 /api/workshop 接口时使用" />
                    </el-form-item>
                </template>

                <el-alert v-else type="info" :closable="false" show-icon
                    title="当前协议无需专用扩展字段，或请按现场文档自行在后端扩展；保存时仅提交通用项。" />
            </el-form>
            <template #footer>
                <span class="dialog-footer">
                    <el-button @click="dialogVisible = false">取消</el-button>
                    <el-button type="primary" @click="saveDevice">保存</el-button>
                </span>
            </template>
        </el-dialog>

        <el-dialog v-model="detailDialogVisible" title="设备详情" width="700px">
            <el-descriptions :column="2" :size="'small'" border>
                <el-descriptions-item label="设备名称">{{
                    detailDevice?.name || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="设备编号">{{
                    detailDevice?.code || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="设备类型">{{
                    detailDevice?.deviceType || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="品牌">{{
                    detailDevice?.brand || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="型号">{{
                    detailDevice?.model || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="协议">{{
                    detailDevice?.protocol || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="主机">{{
                    detailDevice?.ip || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="端口">{{
                    detailDevice?.port ?? "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="连接超时 ms">{{
                    detailDevice?.connectTimeoutMs ?? "-"
                    }}</el-descriptions-item>
                <el-descriptions-item label="读取超时 ms">{{
                    detailDevice?.readTimeoutMs ?? "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'FOCAS'" label="轴标签" :span="2">{{
                    detailDevice?.axisLabels || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'OpcUa'" label="账号">{{
                    detailDevice?.username || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'OpcUa'" label="密码">{{
                    detailDevice?.username ? "******" : "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'OpcUa'" label="EndpointUrl" :span="2">{{
                    detailDevice?.endpointUrl || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'OpcUa'" label="使用安全连接" :span="2">{{
                    detailDevice?.useSecurity || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'OpcUa'" label="自动接受不受信任证书" :span="2">{{
                    detailDevice?.autoAcceptUntrustedCerts || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'OpcUa'" label="拒绝SHA1证书" :span="2">{{
                    detailDevice?.rejectSHA1SignedCertificates || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'OpcUa'" label="抑制Nonce校验错误" :span="2">{{
                    detailDevice?.suppressNonceValidationErrors || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol" label="文件传输协议">{{
                    detailDevice?.transferProtocol
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol" label="传输主机">{{
                    detailDevice?.transferHost || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol" label="传输端口">{{
                    detailDevice?.transferPort ?? "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol === 'SMB'" label="SMB 共享名">{{
                    detailDevice?.transferShareName || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol" label="传输账号">{{
                    detailDevice?.transferUsername || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol" label="传输密码">{{
                    detailDevice?.transferUsername ? "******" : "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol" label="传输连接超时 ms">{{
                    detailDevice?.transferConnectTimeoutMs ?? "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.transferProtocol" label="传输读取超时 ms">{{
                    detailDevice?.transferReadTimeoutMs ?? "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'NCLink'" label="设备唯一标识" :span="2">{{
                    detailDevice?.deviceGuid || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'NCLink'" label="品牌（扩展）" :span="2">{{
                    detailDevice?.nclinkBrand || "-"
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'NCLink'" label="MQTT 代理地址/端口" :span="2">{{
                    detailMqttSummary
                    }}</el-descriptions-item>
                <el-descriptions-item v-if="detailDevice?.protocol === 'NCLink'" label="MQTT 用户名" :span="2">{{
                    detailDevice?.mqttUsername || "-"
                    }}</el-descriptions-item>
            </el-descriptions>
            <template #footer>
                <span class="dialog-footer">
                    <el-button type="primary" @click="detailDialogVisible = false">
                        关闭
                    </el-button>
                </span>
            </template>
        </el-dialog>

        <el-dialog v-model="pointDialogVisible" :title="`点位配置 - ${pointDialogDeviceName}`" width="1200px">
            <div class="point-layout">
                <el-card class="point-tree-panel" shadow="never">
                    <template #header>
                        <div class="tree-title">设备 - 点位树</div>
                    </template>
                    <el-input v-model="pointTreeKeyword" placeholder="搜索设备/点位" class="tree-search" />
                    <el-tree :key="pointTreeRenderKey" ref="pointTreeRef" :data="pointTreeData" node-key="id"
                        :default-expanded-keys="pointExpandedKeys" :expand-on-click-node="false"
                        :current-node-key="selectedPointTreeNodeId" highlight-current
                        :filter-node-method="filterPointTreeNode" @node-click="handlePointTreeNodeClick">
                        <template #default="{ data }">
                            <div class="point-tree-node"
                                :class="{ 'is-selected': selectedPointTreeNodeId === data.id }">
                                <span>{{ data.label }}</span>
                            </div>
                        </template>
                    </el-tree>
                </el-card>

                <el-card shadow="never">
                    <template #header>
                        <div class="point-header">
                            <span>点位列表</span>
                            <div class="point-header-actions">
                                <el-select v-model="refreshMode" size="small" style="width: 120px">
                                    <el-option label="不刷新" value="off" />
                                    <el-option label="5秒刷新" value="5s" />
                                    <el-option label="10秒刷新" value="10s" />
                                </el-select>
                                <el-button size="small" type="primary" @click="handleManualRefresh">
                                    手动刷新
                                </el-button>
                                <el-button size="small" type="success" @click="exportPointAddress">
                                    导出地址
                                </el-button>
                            </div>
                        </div>
                        <div class="point-toolbar">
                            <el-button size="small" type="primary" @click="handleSelectAllPoints">全选</el-button>
                            <el-button size="small" type="warning" @click="handleInvertSelectPoints">反选</el-button>
                            <el-button size="small" @click="handleBatchReadPoints">批量读取</el-button>
                            <el-button size="small" type="success" @click="handleBatchWritePoints">批量写入</el-button>
                        </div>
                    </template>
                    <el-table ref="pointTableRef" v-loading="pointTableFlattenLoading" :data="filteredPointTableData"
                        border @selection-change="handlePointSelectionChange">
                        <el-table-column type="selection" width="46" />
                        <el-table-column prop="path" label="路径" min-width="140" />
                        <el-table-column prop="displayName" label="显示名称" min-width="140" />
                        <el-table-column prop="nodeType" label="节点类型" width="100" />
                        <el-table-column prop="dataType" label="数据类型" width="100" />
                        <el-table-column prop="isReadable" label="可读" width="100" />
                        <el-table-column prop="isWritable" label="可写" width="100" />
                        <el-table-column label="当前值" width="160" fixed="right">
                            <template #default="{ row }">
                                <el-input v-model="row.currentValue" size="small"
                                    :disabled="row.nodeType === 'Folder'" />
                            </template>
                        </el-table-column>
                        <el-table-column label="采集频率" width="130" fixed="right">
                            <template #default="{ row }">
                                <div class="freq-input">
                                    <el-input v-model="row.frequency" size="small" placeholder="500" />
                                    <span class="freq-unit">ms</span>
                                </div>
                            </template>
                        </el-table-column>
                        <el-table-column label="操作" width="140" fixed="right">
                            <template #default="{ row }">
                                <template v-if="row.nodeType === 'Variable'">
                                    <el-button link type="primary" size="small" @click="handleReadSinglePoint(row)">
                                        读取
                                    </el-button>
                                    <el-button link type="warning" size="small" @click="handleWriteSinglePoint(row)">
                                        写入
                                    </el-button>
                                </template>
                                <span v-else class="muted">—</span>
                            </template>
                        </el-table-column>
                    </el-table>
                    <div class="point-table-footer">
                        <el-button type="primary" size="small" @click="handleSavePointConfig">
                            保存
                        </el-button>
                    </div>
                </el-card>
            </div>
            <template #footer>
                <el-button @click="pointDialogVisible = false">关闭</el-button>
            </template>
        </el-dialog>

        <el-dialog v-model="transferDialogVisible" :title="`传输功能 - ${transferDialogDeviceName}`" width="900px">
            <div class="transfer-toolbar transfer-toolbar--form">
                <div class="transfer-row">
                    <span class="transfer-label">设备</span>
                    <el-select v-model="transferForm.deviceId" size="small" style="width: 200px" filterable>
                        <el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" />
                    </el-select>
                    <span class="transfer-label">方向</span>
                    <el-select v-model="transferForm.direction" size="small" style="width: 140px">
                        <el-option label="上传到设备" value="upload" />
                        <el-option label="从设备下载" value="download" />
                    </el-select>
                </div>
                <div class="transfer-row">
                    <span class="transfer-label">传输方式</span>
                    <el-tag size="small" :type="transferChannelTagType">
                        {{ transferChannelLabel }}
                    </el-tag>
                    <span class="transfer-channel-detail">{{ transferChannelDetail }}</span>
                </div>
                <div class="transfer-row">
                    <span class="transfer-label">设备端路径</span>
                    <el-input
                        v-model="transferRemotePath"
                        size="small"
                        style="width: 280px"
                        clearable
                        placeholder="可手工输入，如 O0001 或 selftest/O99999"
                        @input="onTransferRemotePathInput" />
                    <el-button size="small" @click="openRemotePathPicker">选择设备文件</el-button>
                    <div class="transfer-path-result">
                        <el-tooltip
                            placement="top"
                            :show-after="200"
                            :disabled="!transferRemotePath.trim()"
                            :content="transferRemotePath">
                            <span class="transfer-path-result__path">{{
                                transferRemotePath || "未选择设备端路径"
                            }}</span>
                        </el-tooltip>
                        <el-tag
                            v-if="transferRemotePathKindTag"
                            class="transfer-path-result__tag"
                            size="small"
                            type="info"
                            effect="plain">
                            {{ transferRemotePathKindTag }}
                        </el-tag>
                    </div>
                </div>
                <div
                    v-if="transferForm.direction === 'download' && transferRemotePathPickedKind === 'batch' && transferBatchSelectionLabels.length"
                    class="transfer-row">
                    <span class="transfer-label">已选批量文件</span>
                    <div class="transfer-batch-result">
                        <el-tooltip
                            placement="top"
                            :show-after="200"
                            :content="transferBatchSelectionLabels.join('\n')">
                            <span class="transfer-batch-result__text">
                                {{ transferBatchSelectionPreview }}
                            </span>
                        </el-tooltip>
                    </div>
                </div>
                <div v-if="transferForm.direction === 'upload'" class="transfer-row">
                    <span class="transfer-label">本地文件</span>
                    <el-button size="small" @click="triggerSelectUploadPath">
                        选择文件
                    </el-button>
                    <span class="transfer-file-name">{{ transferSelectedUploadLabel }}</span>
                    <input ref="uploadFileInputRef" type="file" multiple style="display: none"
                        @change="handleUploadFilesChange" />
                </div>
                <div class="transfer-row transfer-row--actions">
                    <el-button size="small" type="primary" :loading="transferSubmitting" @click="startTransfer">
                        开始传输
                    </el-button>
                </div>
            </div>

            <el-table v-loading="transferHistoryLoading" :data="transferHistory" border size="small">
                <el-table-column prop="time" label="时间" min-width="150" />
                <el-table-column prop="fileName" label="文件名" min-width="170" />
                <el-table-column prop="directionLabel" label="方向" width="110" />
                <el-table-column prop="fileSizeLabel" label="文件大小" width="120" />
                <el-table-column prop="durationLabel" label="传输时间" width="120" />
                <el-table-column prop="throughputLabel" label="传输效率" width="130" />
                <el-table-column prop="progress" label="进度" width="100" />
                <el-table-column label="状态" width="100">
                    <template #default="{ row }">
                        <el-tag :type="row.statusType" size="small">
                            {{ row.status }}
                        </el-tag>
                    </template>
                </el-table-column>
                <el-table-column prop="message" label="消息" min-width="140" />
            </el-table>

            <template #footer>
                <el-button @click="transferDialogVisible = false">关闭</el-button>
            </template>
        </el-dialog>
        <el-dialog v-model="collectionDialogVisible" :title="`采集数据 - ${collectionDialogDeviceName || '未选择设备'}`" width="980px">
            <el-table :data="collectionRows" border size="small" height="420">
                <el-table-column prop="displayName" label="显示名称" min-width="180" />
                <el-table-column prop="path" label="路径" min-width="260" />
                <el-table-column prop="dataType" label="数据类型" width="120" />
                <el-table-column prop="value" label="值" min-width="140" />
                <el-table-column prop="status" label="状态" width="100" />
                <el-table-column prop="time" label="采集时间" width="180" />
            </el-table>
            <template #footer>
                <el-button @click="collectionDialogVisible = false">关闭</el-button>
                <el-button type="warning" @click="stopCollection(collectionDeviceId)">停止当前设备采集</el-button>
                <el-button type="primary" @click="openCollectionHistoryDialog(collectionDeviceId)">历史采集记录</el-button>
            </template>
        </el-dialog>
        <el-dialog v-model="collectionHistoryDialogVisible"
            :title="`历史采集记录 - ${collectionHistoryDeviceName || '未选择设备'}`" width="1100px">
            <div class="collection-history-toolbar">
                <el-radio-group v-model="collectionHistoryMode">
                    <el-radio-button label="day">日</el-radio-button>
                    <el-radio-button label="week">周</el-radio-button>
                    <el-radio-button label="month">月</el-radio-button>
                    <el-radio-button label="custom">日期起止</el-radio-button>
                </el-radio-group>
                <el-date-picker v-if="collectionHistoryMode === 'custom'" v-model="collectionHistoryCustomDateRange"
                    type="daterange" range-separator="至" start-placeholder="开始日期" end-placeholder="结束日期"
                    format="YYYY-MM-DD" value-format="YYYY-MM-DD" />
                <el-button type="primary" :loading="collectionHistoryLoading" @click="queryCollectionHistory">
                    查询
                </el-button>
            </div>
            <el-table v-loading="collectionHistoryLoading" :data="collectionHistoryRows" border size="small" height="430">
                <el-table-column prop="displayName" label="显示名称" min-width="160" />
                <el-table-column prop="path" label="路径" min-width="240" />
                <el-table-column prop="dataType" label="数据类型" width="110" />
                <el-table-column prop="value" label="值" min-width="120" />
                <el-table-column prop="quality" label="质量" width="100" />
                <el-table-column prop="status" label="状态" width="90" />
                <el-table-column prop="errorMessage" label="错误信息" min-width="140" />
                <el-table-column prop="time" label="采集时间" width="180" />
            </el-table>
            <div class="collection-history-pagination">
                <el-pagination v-model:current-page="collectionHistoryCurrentPage"
                    v-model:page-size="collectionHistoryPageSize"
                    :page-sizes="[10, 20, 50, 100]" layout="total, sizes, prev, pager, next, jumper"
                    :total="collectionHistoryTotal" @size-change="handleCollectionHistorySizeChange"
                    @current-change="handleCollectionHistoryCurrentChange" />
            </div>
            <template #footer>
                <el-button @click="collectionHistoryDialogVisible = false">关闭</el-button>
            </template>
        </el-dialog>
        <el-dialog v-model="remotePathPickerVisible" title="选择设备端文件" width="560px" destroy-on-close>
            <div class="remote-path-picker">
                <el-tree
                    :key="remotePathPickerTreeKey"
                    ref="remotePathTreeRef"
                    :lazy="remotePathPickerLazyAddressSpace"
                    :data="remotePathPickerLazyAddressSpace ? remotePathPickerLazyTreeRootData : remotePathTreeData"
                    :load="remotePathPickerLazyAddressSpace ? loadRemotePathPickerLazy : undefined"
                    node-key="key"
                    :show-checkbox="remotePathPickerShowCheckboxes"
                    :default-expanded-keys="remotePathExpandedKeys"
                    :expand-on-click-node="!remotePathPickerShowCheckboxes"
                    :current-node-key="remotePathSelectedNodeKey"
                    @node-click="handleRemotePathNodeClick"
                    @check-change="onRemotePathPickerCheckChange">
                    <template #default="{ data }">
                        <div class="remote-path-node">
                            <el-icon>
                                <Folder v-if="data.nodeType === 'folder'" />
                                <Document v-else />
                            </el-icon>
                            <div class="remote-path-node__text">
                                <span class="remote-path-node__label">{{ data.label }}</span>
                            </div>
                        </div>
                    </template>
                </el-tree>
                <div v-if="remotePathPickerShowCheckboxes" class="remote-path-preview">
                    <span class="remote-path-preview__label">勾选结果：</span>
                    <span class="remote-path-preview__value">{{ remotePathPickerCheckHint }}</span>
                </div>
                <div class="remote-path-preview">
                    <span class="remote-path-preview__label">当前节点路径：</span>
                    <span class="remote-path-preview__value">{{ remotePathDraft || "未选择" }}</span>
                </div>
                <div class="remote-path-preview">
                    <span class="remote-path-preview__label">选择规则：</span>
                    <span class="remote-path-preview__value">{{ transferRemotePathRuleHint }}</span>
                </div>
            </div>
            <template #footer>
                <el-button @click="remotePathPickerVisible = false">取消</el-button>
                <el-button type="primary" @click="confirmRemotePath">确定</el-button>
            </template>
        </el-dialog>

        <!-- NC-Link 诊断：Probe 自报模型 / 数据项 / 采样通道（真实接口 /api/nclink/*） -->
        <el-dialog v-model="nclinkDialogVisible" title="NC-Link 诊断" width="880px">
            <el-form :inline="true">
                <el-form-item label="NC-Link 设备">
                    <el-select
                        v-model="nclinkDeviceId"
                        placeholder="选择 NC-Link 协议设备"
                        style="width: 340px"
                    >
                        <el-option
                            v-for="d in nclinkDevices"
                            :key="d.id"
                            :label="`${d.name}（${d.protocol} · ${d.ip}:${d.port}）`"
                            :value="d.id"
                        />
                    </el-select>
                </el-form-item>
                <el-form-item>
                    <el-button
                        type="primary"
                        :loading="nclinkLoading"
                        :disabled="!nclinkDeviceId"
                        @click="loadNclinkDiagnostics"
                    >
                        读取 Probe 模型
                    </el-button>
                </el-form-item>
            </el-form>
            <el-alert
                v-if="nclinkError"
                type="error"
                :closable="false"
                show-icon
                :title="nclinkError"
                style="margin-bottom: 12px"
            />
            <template v-if="nclinkProbe">
                <el-descriptions :column="3" border size="small" style="margin-bottom: 12px">
                    <el-descriptions-item label="设备模型 ID">{{ nclinkProbe.id }}</el-descriptions-item>
                    <el-descriptions-item label="GUID">{{ nclinkProbe.guid || "-" }}</el-descriptions-item>
                    <el-descriptions-item label="版本">{{ nclinkProbe.version || "-" }}</el-descriptions-item>
                    <el-descriptions-item label="数据项数量">{{ nclinkProbe.dataItemCount }}</el-descriptions-item>
                    <el-descriptions-item label="采样通道数量">{{ nclinkProbe.sampleChannelCount }}</el-descriptions-item>
                </el-descriptions>
                <el-tabs v-model="nclinkTab">
                    <el-tab-pane label="数据项（DataItems）" name="dataitems">
                        <el-input
                            v-model="nclinkFilter"
                            placeholder="按 ID / 名称过滤"
                            clearable
                            style="margin-bottom: 8px; width: 280px"
                        />
                        <el-table :data="filteredNclinkItems" border size="small" max-height="380">
                            <el-table-column prop="id" label="ID" min-width="180" show-overflow-tooltip />
                            <el-table-column prop="name" label="名称" min-width="150" show-overflow-tooltip />
                            <el-table-column prop="type" label="类型" width="110" />
                            <el-table-column label="可写" width="70">
                                <template #default="scope">
                                    <el-tag v-if="scope.row.settable" size="small" type="warning">写</el-tag>
                                    <span v-else>-</span>
                                </template>
                            </el-table-column>
                            <el-table-column prop="unit" label="单位" width="80" />
                            <el-table-column prop="componentPath" label="组件路径" min-width="150" show-overflow-tooltip />
                        </el-table>
                    </el-tab-pane>
                    <el-tab-pane label="采样通道" name="channels">
                        <pre
                            style="max-height: 380px; overflow: auto; background: var(--el-fill-color-light); padding: 12px; margin: 0"
                        >{{ JSON.stringify(nclinkProbe.sampleChannels, null, 2) }}</pre>
                    </el-tab-pane>
                </el-tabs>
            </template>
            <el-empty
                v-else-if="!nclinkLoading && !nclinkError"
                description="选择设备后读取 Probe 自报模型（需 Industrial IoT 可连接该机床）"
                :image-size="70"
            />
            <template #footer>
                <el-button @click="nclinkDialogVisible = false">关闭</el-button>
            </template>
        </el-dialog>
    </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted, onBeforeUnmount, nextTick } from "vue";
import { Plus, DocumentAdd, Download, Upload, Folder, Document, Connection, Cpu } from "@element-plus/icons-vue";
import {
    machineConnectionDiagnosticsApi,
    type NCLinkProbeModel,
} from "@/api/machineConnectionDiagnostics";
import type { ElTree, ElTable } from "element-plus";
import { ElMessage, ElMessageBox } from "element-plus";
import {
    machineConnectionDevicesApi,
    type DeviceDto,
    type DeviceTypeApi,
} from "@/api/machineConnectionDevices";
import {
    machineConnectionPointsApi,
    type AddressNode,
} from "@/api/machineConnectionPoints";
import { datacollectionApi } from "@/api/datacollection";
import {
    telemetryInfluxApi,
    type InfluxTelemetryHistoryItem,
} from "@/api/telemetryInflux";
import {
    machineConnectionProgramTransferApi,
    type ProgramTransferResponse,
} from "@/api/machineConnectionProgramTransfer";
import { buildProgramTransferConfig } from "./deviceTransferConfig";

const machineConnectionPort =
    import.meta.env.VITE_MACHINE_CONNECTION_PORT ?? "5087";
const industrialIotPort =
    import.meta.env.VITE_INDUSTRIAL_IOT_PORT ?? "5173";

function formatAxiosErrorBody(data: unknown): string {
    if (data == null) return "";
    if (typeof data === "string")
        return data.length > 400 ? `${data.slice(0, 400)}…` : data;
    try {
        const s = JSON.stringify(data);
        return s.length > 400 ? `${s.slice(0, 400)}…` : s;
    } catch {
        return String(data);
    }
}

type UiStatus = "online" | "offline" | "error";

interface DeviceUi {
    id: string;
    name: string;
    brand: string;
    brandKey: string;
    code: string;
    model: string;
    line: string;
    ip: string;
    port: number;
    protocol: string;
    status: UiStatus;
    lastCommTime: string;
    station: number;
    baudRate: number;
    connectTimeoutMs: number;
    readTimeoutMs: number;
    axisLabels: string;
    authType: string;
    username?: string;
    password?: string;
    deviceType: DeviceTypeApi;
    uploadPath?: string;
    opcuaSecurityPolicy?: string;
    opcuaSecurityMode?: string;
    opcuaNamespaceIndex?: number;
    opcuaServerUri?: string;
    /** extendedProperties（OpcUa） */
    useSecurity: string;
    autoAcceptUntrustedCerts: string;
    rejectSHA1SignedCertificates: string;
    suppressNonceValidationErrors: string;
    endpointUrl: string;
    transferProtocol: string;
    transferHost: string;
    transferPort: number;
    transferUsername: string;
    transferPassword: string;
    transferShareName: string;
    transferConnectTimeoutMs: number;
    transferReadTimeoutMs: number;
    /** extendedProperties（NCLink） */
    deviceGuid: string;
    nclinkBrand: string;
    mqttBrokerHost: string;
    mqttBrokerPort: string;
    mqttUsername: string;
    /** 原始 extendedProperties，用于编辑时回填 NCLinkApi / GskWebServer 等动态字段 */
    extendedProperties?: Record<string, string | undefined>;
}

/** 与左侧设备树 brand-* 节点一致，用于筛选与「新增设备」预填 */
const BRAND_KEY_TO_FORM_LABEL: Record<string, string> = {
    mazak: "马扎克（Mazak）",
    haas: "哈斯（Haas）",
    brother: "兄弟（Brother）",
    makino: "牧野（Makino）",
    jingdiao: "北京精雕",
    huazhong: "华中数控",
    guangzhou: "广州数控",
    siemens: "西门子",
    fanuc: "法那科",
    heidenhain: "海德汉",
};

function inferBrandKey(brand: string): string {
    const b = brand.toLowerCase();
    if (b.includes("mazak") || b.includes("马扎克")) return "mazak";
    if (b.includes("haas") || b.includes("哈斯")) return "haas";
    if (b.includes("brother") || b.includes("兄弟")) return "brother";
    if (b.includes("makino") || b.includes("牧野")) return "makino";
    if (b.includes("精雕") || b.includes("jingdiao")) return "jingdiao";
    if (b.includes("华中")) return "huazhong";
    if (b.includes("广州数控") || b.includes("广数")) return "guangzhou";
    if (b.includes("西门子") || b.includes("siemens")) return "siemens";
    if (b.includes("fanuc") || b.includes("法那科") || b.includes("发那科")) return "fanuc";
    if (b.includes("heidenhain") || b.includes("海德汉")) return "heidenhain";
    return "other";
}

function mapStatus(s: string): UiStatus {
    const v = String(s ?? "").trim().toLowerCase();
    if (!v) return "offline";

    // 在线
    if (v === "online" || v === "on" || v === "connected") return "online";

    // 离线
    if (
        v === "offline" ||
        v === "off" ||
        v === "disconnected" ||
        v === "unknown" ||
        v === "unavailable"
    )
        return "offline";

    // 异常
    if (
        v === "error" ||
        v === "abnormal" ||
        v === "exception" ||
        v === "fault" ||
        v === "alarm"
    )
        return "error";

    // 兼容后端可能返回的枚举（如 Online / Offline / Error）
    if (v.includes("online")) return "online";
    if (v.includes("off")) return "offline";
    if (v.includes("err") || v.includes("abnorm") || v.includes("except"))
        return "error";

    return "offline";
}

function formatSeenAt(iso: string | null | undefined): string {
    if (!iso) return "-";
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
}

function mapDtoToUi(d: DeviceDto): DeviceUi {
    const ext = d.extendedProperties ?? {};
    const brandKey = ext.uiBrandKey ?? inferBrandKey(d.brand);
    const transfer = d.transfer;
    return {
        id: d.id,
        name: d.name,
        brand: d.brand,
        brandKey,
        code: ext.DeviceCode ?? ext.Code ?? ext.code ?? "",
        model: d.model,
        line: ext.Line ?? ext.line ?? "",
        ip: d.host,
        port: d.port,
        protocol: d.protocol,
        status: mapStatus(d.status),
        lastCommTime: formatSeenAt(d.lastSeenAt),
        station: ext.Station ? Number(ext.Station) : 1,
        baudRate: ext.BaudRate ? Number(ext.BaudRate) : 9600,
        connectTimeoutMs: d.connectTimeoutMs ?? 10000,
        readTimeoutMs: d.readTimeoutMs ?? 5000,
        axisLabels: ext.AxisLabels ?? "",
        authType: d.username ? "password" : "anonymous",
        username: d.username ?? "",
        password: "",
        deviceType: d.type,
        uploadPath: ext.UploadPath ?? "",
        opcuaSecurityPolicy: ext.OpcUaSecurityPolicy ?? "None",
        opcuaSecurityMode: ext.OpcUaSecurityMode ?? "None",
        opcuaNamespaceIndex: ext.OpcUaNamespaceIndex
            ? Number(ext.OpcUaNamespaceIndex)
            : 0,
        opcuaServerUri: ext.OpcUaServerUri ?? "",
        useSecurity: String(ext.UseSecurity ?? ""),
        autoAcceptUntrustedCerts: String(ext.AutoAcceptUntrustedCerts ?? ""),
        rejectSHA1SignedCertificates: String(ext.RejectSHA1SignedCertificates ?? "false"),
        suppressNonceValidationErrors: String(ext.SuppressNonceValidationErrors ?? "true"),
        endpointUrl: String(ext.EndpointUrl ?? ""),
        transferProtocol: String(transfer?.protocol ?? ""),
        transferHost: String(transfer?.host ?? ""),
        transferPort: Number(transfer?.port ?? 21),
        transferUsername: String(transfer?.username ?? ""),
        transferPassword: "",
        transferShareName: String(transfer?.extendedProperties?.ShareName ?? ""),
        transferConnectTimeoutMs: Number(transfer?.connectTimeoutMs ?? 10000),
        transferReadTimeoutMs: Number(transfer?.readTimeoutMs ?? 5000),
        deviceGuid: String(ext.DeviceGuid ?? ""),
        nclinkBrand: String(ext.Brand ?? ""),
        mqttBrokerHost: String(ext.MqttBrokerHost ?? ""),
        mqttBrokerPort: String(ext.MqttBrokerPort ?? ""),
        mqttUsername: String(ext.MqttUsername ?? ""),
        extendedProperties: ext as Record<string, string | undefined>,
    };
}

function buildExtendedProps(form: Record<string, unknown>): Record<string, string> {
    const ext: Record<string, string> = {};
    const protocol = String(form.protocol ?? "");
    const code = String(form.code ?? "").trim();
    if (code) {
        ext.DeviceCode = code;
        ext.Code = code;
    }
    const line = String(form.line ?? "").trim();
    if (line) ext.Line = line;
    const brand = String(form.brand ?? "").trim();
    if (brand) ext.uiBrandKey = inferBrandKey(brand);

    if (protocol === "FOCAS") {
        const axisLabels = String(form.axisLabels ?? "").trim();
        if (axisLabels) ext.AxisLabels = axisLabels;
        return ext;
    }

    if (protocol === "OpcUa") {
        const endpointUrl = String(form.endpointUrl ?? "").trim();
        if (endpointUrl) {
            ext.EndpointUrl = endpointUrl;
        } else {
            const host = String(form.ip ?? "").trim();
            const port = Number(form.port ?? 0);
            if (host && Number.isFinite(port) && port > 0) {
                ext.EndpointUrl = `opc.tcp://${host}:${port}`;
            }
        }
        const us = String(form.useSecurity ?? "").trim();
        if (us) ext.UseSecurity = us;
        const ac = String(form.autoAcceptUntrustedCerts ?? "").trim();
        if (ac) ext.AutoAcceptUntrustedCerts = ac;
        const rejectSha1 = String(form.rejectSHA1SignedCertificates ?? "").trim();
        if (rejectSha1) ext.RejectSHA1SignedCertificates = rejectSha1;
        const suppressNonce = String(form.suppressNonceValidationErrors ?? "").trim();
        if (suppressNonce) ext.SuppressNonceValidationErrors = suppressNonce;
        return ext;
    }

    if (protocol === "NCLink") {
        const dg = String(form.deviceGuid ?? "").trim();
        if (dg) ext.DeviceGuid = dg;
        const nb = String(form.nclinkBrand ?? "").trim();
        if (nb) ext.Brand = nb;
        const mh = String(form.mqttBrokerHost ?? "").trim();
        if (mh) ext.MqttBrokerHost = mh;
        const mp = String(form.mqttBrokerPort ?? "").trim();
        if (mp) ext.MqttBrokerPort = mp;
        const mu = String(form.mqttUsername ?? "").trim();
        if (mu) ext.MqttUsername = mu;
        const mpw = String(form.mqttPassword ?? "").trim();
        if (mpw) ext.MqttPassword = mpw;
        return ext;
    }

    if (protocol === "NCLinkApi") {
        const did = String(form.ncLinkApiDeviceId ?? "").trim();
        if (did) ext.DeviceId = did;
        const bu = String(form.ncLinkApiBaseUrl ?? "").trim();
        if (bu) ext.ApiBaseUrl = bu;
        return ext;
    }

    if (protocol === "GskWebServer") {
        const sn = String(form.gskDeviceSn ?? "").trim();
        if (sn) ext.DeviceSn = sn;
        const scheme = String(form.gskScheme ?? "").trim();
        if (scheme) ext.Scheme = scheme;
        const mgmt = String(form.gskManagementBaseUrl ?? "").trim();
        if (mgmt) ext.ManagementBaseUrl = mgmt;
        const tok = String(form.gskWorkshopAuthToken ?? "").trim();
        if (tok) ext.WorkshopAuthToken = tok;
        return ext;
    }

    return ext;
}

const devices = ref<DeviceUi[]>([]);
const devicesLoading = ref(false);
const devicesError = ref<string | null>(null);
const deviceImportInputRef = ref<HTMLInputElement | null>(null);

async function loadDevices() {
    devicesLoading.value = true;
    devicesError.value = null;
    try {
        const list = await machineConnectionDevicesApi.list();
        if (!Array.isArray(list)) {
            console.warn("设备列表接口返回非数组:", list);
            devices.value = [];
            devicesError.value =
                "设备列表接口返回格式异常（期望 JSON 数组），请检查是否命中了前端页面而非 API";
            return;
        }
        devices.value = list.map(mapDtoToUi);
    } catch (e: unknown) {
        console.error(e);
        devices.value = [];
        const err = e as {
            message?: string;
            response?: { status?: number; data?: unknown };
        };
        if (err.response?.status === 502) {
            const data502 = err.response.data as { error?: string } | undefined;
            const msg502 = data502?.error?.trim();
            devicesError.value =
                msg502 ||
                `网关返回 502：无法连接 Industrial IoT。请将 MachineConnectionApi 的 IndustrialIoT:BaseUrl 设为 http://localhost:${industrialIotPort}/ 并确认 IoT 已运行`;
        } else if (err.response?.status === 404) {
            devicesError.value =
                `请求 404：未找到设备接口。若 .env.production 里 VITE_MACHINE_CONNECTION_API=/machine-connection，请在静态站点（IIS/Nginx 等）把 /machine-connection 反代到 MachineConnectionApi；或改为网关完整地址（如 http://部署机IP:5087）后重新 npm run build。当前 axios base 为：${import.meta.env.VITE_MACHINE_CONNECTION_API ?? "(未设置，使用默认 /machine-connection)"}`;
        } else if (err.response?.status === 500) {
            const detail = formatAxiosErrorBody(err.response.data);
            devicesError.value = detail
                ? `服务器 500：${detail}。若刚修改过网关配置，请重启 MachineConnectionApi；并请用 Swagger 直接访问 IoT 的 GET /api/Devices 对比`
                : "服务器返回 500，请查看 MachineConnectionApi 与 Industrial IoT 控制台异常栈";
        } else if (
            err.message === "Network Error" ||
            (typeof err.message === "string" && err.message.includes("ECONNREFUSED"))
        ) {
            devicesError.value = `无法连接网关（连接被拒绝）。请使用 npm run dev，并确认 MachineConnectionApi 已在 http://localhost:${machineConnectionPort} 监听，且 Vite 代理目标（VITE_MACHINE_CONNECTION_PROXY_TARGET）与该端口一致`;
        } else {
            devicesError.value = err.message
                ? `加载失败：${err.message}`
                : "加载设备列表失败";
        }
        ElMessage.error(devicesError.value);
    } finally {
        devicesLoading.value = false;
    }
}

onMounted(() => {
    void loadDevices();
});

// 分页数据
const currentPage = ref(1);
const pageSize = ref(10);
const searchKeyword = ref("");
const treeKeyword = ref("");
const selectedTreeNodeId = ref("all");
const treeRef = ref<InstanceType<typeof ElTree>>();
const pointDialogVisible = ref(false);
const pointDialogDeviceName = ref("");
const pointDialogDeviceId = ref<string>("");
const transferDialogVisible = ref(false);
const transferDialogDeviceName = ref("");
const collectionDialogVisible = ref(false);
const collectionDialogDeviceName = ref("");
const refreshMode = ref("off");
const pointAutoRefreshTimerId = ref<number | null>(null);
const pointTreeKeyword = ref("");
const pointTreeRef = ref<InstanceType<typeof ElTree>>();
const pointTreeRenderKey = ref(0);
const pointTableRef = ref<InstanceType<typeof ElTable>>();
const selectedPointTreeNodeId = ref<string>("/");
const pointExpandedKeys = ref<string[]>(["/"]);
const selectedPointRows = ref<PointRow[]>([]);
/** 当前设备在 MachineCollection.datacollection 中已存在的路径（用于默认勾选） */
const savedPathsInDb = ref<Set<string>>(new Set());
/** 当前设备在数据库中已保存的点位配置（用于回填采集频率） */
const savedPointConfigByPath = ref<Map<string, { collectionFrequency: number }>>(new Map());

const deviceTree = [
    {
        id: "all",
        label: "全部设备",
        children: [
            { id: "brand-mazak", label: "马扎克（Mazak）" },
            { id: "brand-haas", label: "哈斯（Haas）" },
            { id: "brand-brother", label: "兄弟（Brother）" },
            { id: "brand-makino", label: "牧野（Makino）" },
            { id: "brand-jingdiao", label: "北京精雕" },
            { id: "brand-huazhong", label: "华中数控" },
            { id: "brand-guangzhou", label: "广州数控" },
            { id: "brand-siemens", label: "西门子" },
            { id: "brand-fanuc", label: "法那科" },
            { id: "brand-heidenhain", label: "海德汉" },
        ],
    },
];

// 过滤设备数据（树 + 搜索，不含分页）
const devicesFiltered = computed(() => {
    let result = devices.value;

    if (selectedTreeNodeId.value.startsWith("brand-")) {
        const brand = selectedTreeNodeId.value.replace("brand-", "");
        result = result.filter((device) => device.brandKey === brand);
    }

    if (searchKeyword.value) {
        const keyword = searchKeyword.value.toLowerCase();
        result = result.filter(
            (device) =>
                device.name.toLowerCase().includes(keyword) ||
                device.code.toLowerCase().includes(keyword) ||
                device.ip.includes(keyword) ||
                device.protocol.toLowerCase().includes(keyword),
        );
    }

    return result;
});

const totalDevices = computed(() => devicesFiltered.value.length);

const filteredDevices = computed(() => {
    const startIndex = (currentPage.value - 1) * pageSize.value;
    return devicesFiltered.value.slice(startIndex, startIndex + pageSize.value);
});

watch(treeKeyword, (value) => {
    treeRef.value?.filter(value);
});

const filterTreeNode = (value: string, data: { label: string }) => {
    if (!value) return true;
    return data.label.includes(value);
};

const handleTreeNodeClick = (data: { id: string }) => {
    selectedTreeNodeId.value = data.id;
    currentPage.value = 1;
};

type PointTreeNode = {
    id: string;
    label: string;
    nodeType: "Folder" | "Variable";
    path: string;
    dataType?: string;
    unit?: string;
    isReadable?: boolean;
    isWritable?: boolean;
    sourceId?: string;
    children?: PointTreeNode[];
    _loaded?: boolean;
};

type PointRow = {
    id: string;
    path: string;
    displayName: string;
    nodeType: "Folder" | "Variable";
    dataType: string;
    isReadable: boolean;
    isWritable: boolean;
    children: string;
    enabled: boolean;
    name: string;
    type: string;
    address: string;
    sourceId?: string;
    currentValue: string;
    unit: string;
    frequency: string;
    multiplier: string;
    desc: string;
    writable: boolean;
};

type SavedCollectionPoint = {
    address: string;
    dataType: string;
    displayName: string;
    path: string;
    collectionFrequency: number;
};

type CollectionRow = {
    displayName: string;
    path: string;
    dataType: string;
    value: string;
    status: string;
    time: string;
};

type CollectionHistoryMode = "day" | "week" | "month" | "custom";
type DateStringRange = [string, string];
type CollectionHistoryRow = CollectionRow & {
    quality: string;
    errorMessage: string;
};

const pointTreeData = ref<PointTreeNode[]>([
    {
        id: "/",
        label: "地址空间",
        nodeType: "Folder",
        path: "/",
        children: [],
        _loaded: false,
    },
]);

const pointTableData = ref<PointRow[]>([]);
/** 父节点「展开整棵子树」到右侧表格时拉取子层地址空间，避免无反馈 */
const pointTableFlattenLoading = ref(false);
/** 单棵子树内最多铺平的变量行数，防止根目录全量扫爆 */
const POINT_FLATTEN_MAX_VARIABLES = 5000;
const POINT_FLATTEN_MAX_FOLDER_STEPS = 2000;
const savedCollectionPointsByDevice = ref<Record<string, SavedCollectionPoint[]>>({});
const collectionDeviceId = ref("");
const pointDialogDeviceProtocol = ref("");
const collectionRows = ref<CollectionRow[]>([]);
const collectionRowsByDevice = ref<Record<string, CollectionRow[]>>({});
const collectionLoading = ref(false);
const collectionTimerIdsByDevice = ref<Record<string, number[]>>({});
const collectingDeviceIds = ref<string[]>([]);
const collectionHistoryDialogVisible = ref(false);
const collectionHistoryDeviceId = ref("");
const collectionHistoryDeviceName = ref("");
const collectionHistoryLoading = ref(false);
const collectionHistoryMode = ref<CollectionHistoryMode>("day");
const collectionHistoryCustomDateRange = ref<DateStringRange | []>([]);
const collectionHistoryRows = ref<CollectionHistoryRow[]>([]);
const collectionHistoryCurrentPage = ref(1);
const collectionHistoryPageSize = ref(20);
const collectionHistoryTotal = ref(0);

const filteredPointTableData = computed(() => pointTableData.value);

watch(pointTreeKeyword, (value) => {
    pointTreeRef.value?.filter(value);
});

const filterPointTreeNode = (value: string, data: { label: string }) => {
    if (!value) return true;
    return data.label.includes(value);
};

const handlePointTreeNodeClick = async (data: PointTreeNode) => {
    selectedPointTreeNodeId.value = data.id;

    if (!pointDialogDeviceId.value) return;

    // Folder：懒加载左侧子节点；右侧列表递归收集该节点下**所有** Variable 叶子（含子目录）
    if (data.nodeType === "Folder") {
        if (!data._loaded) {
            await loadAddressChildren(data, { silent: true });
        }
        const deviceId = pointDialogDeviceId.value;
        pointTableFlattenLoading.value = true;
        try {
            const allVars = await flattenAllDescendantVariableNodes(deviceId, data.path);
            if (allVars.length >= POINT_FLATTEN_MAX_VARIABLES) {
                ElMessage.warning(
                    `子树变量较多，已最多加载前 ${POINT_FLATTEN_MAX_VARIABLES} 条，请缩小左侧所选目录`,
                );
            } else if (allVars.length === 0) {
                ElMessage.info("该目录子树中未找到变量（Variable）节点，可能仅含空目录。");
            }
            pointTableData.value = allVars.map(mapVariableNodeToRow);
        } catch {
            // 用户要求：父节点批量加载异常时不弹错误提示，保持界面静默。
            pointTableData.value = [];
        } finally {
            pointTableFlattenLoading.value = false;
        }
        await applySavedPathsToTable();
        return;
    }

    // Variable：表格只显示一个
    pointTableData.value = [mapVariableNodeToRow(data)];
    await applySavedPathsToTable();
};

const openPointDialog = async (device: { id: string; name: string; protocol?: string }) => {
    pointDialogDeviceName.value = device.name;
    pointDialogDeviceId.value = device.id;
    pointDialogDeviceProtocol.value = device.protocol ?? "";
    selectedPointTreeNodeId.value = "/";
    pointDialogVisible.value = true;
    savedPathsInDb.value = new Set();

    // 打开即加载根节点
    const root = pointTreeData.value[0];
    if (!root) return;
    root.children = [];
    root._loaded = false;
    pointTableData.value = [];
    selectedPointRows.value = [];
    await loadAddressChildren(root);
    pointExpandedKeys.value = collectFirstLevelExpandedKeys(root);
    pointTreeRenderKey.value += 1;
    await expandPointTreeNodes(pointExpandedKeys.value);
    await refreshSavedPathsFromDb({ silent: true });
    await handlePointTreeNodeClick(root);
};

const stopPointAutoRefresh = () => {
    if (pointAutoRefreshTimerId.value != null) {
        window.clearInterval(pointAutoRefreshTimerId.value);
        pointAutoRefreshTimerId.value = null;
    }
};

const startPointAutoRefresh = (ms: number) => {
    stopPointAutoRefresh();
    pointAutoRefreshTimerId.value = window.setInterval(() => {
        void handleManualRefresh({ silent: true });
    }, ms);
};

const handleManualRefresh = async (options: { silent?: boolean } = {}) => {
    const { silent = false } = options;
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) return;

    const targets = pointTableData.value.filter((r) => r.enabled);
    if (targets.length === 0) {
        if (!silent) {
            ElMessage.warning("请先选择至少一个启用点位");
        }
        return;
    }
    try {
        const resp = await machineConnectionPointsApi.readTags(deviceId, {
            tags: targets.map((t) => ({
                address: t.address,
                dataType: t.type,
                sourceId: t.sourceId,
            })),
        });
        const map = new Map(resp.tags.map((t) => [normalizeTagAddress(t.address), t]));
        for (const row of pointTableData.value) {
            const r = map.get(normalizeTagAddress(row.address));
            if (!r) continue;
            row.currentValue =
                r.errorMessage?.trim()
                    ? `错误: ${r.errorMessage}`
                    : stringifyTagValue(r.value);
        }
        if (!silent) {
            ElMessage.success("点位数据已刷新");
        }
    } catch (e: unknown) {
        if (!silent) {
            const ax = e as { response?: { data?: { error?: string } } };
            ElMessage.error(ax.response?.data?.error ?? "点位读取失败");
        }
    }
};

const exportPointAddress = async () => {
    if (selectedPointRows.value.length === 0) {
        ElMessage.warning("请先勾选要导出的点位");
        return;
    }
    const header = ["路径", "显示名称", "节点类型", "数据类型", "可读", "可写", "当前值"];
    const lines = selectedPointRows.value.map((row) => [
        row.path,
        row.displayName,
        row.nodeType,
        row.dataType,
        row.isReadable,
        row.isWritable,
        row.currentValue,
    ]);
    const csv =
        [header, ...lines]
            .map((line) => line.map(escapeCsvField).join(","))
            .join("\r\n");
    const blob = new Blob([`\uFEFF${csv}`], { type: "text/csv;charset=utf-8;" });
    const fileName = `selected_points_${new Date()
        .toISOString()
        .replace(/[:T]/g, "-")
        .slice(0, 19)}.csv`;
    downloadBlob(blob, fileName);
    ElMessage.success(`已导出 ${selectedPointRows.value.length} 个点位`);
};

const handleReadSinglePoint = async (row: PointRow) => {
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) return;
    if (row.nodeType === "Folder") {
        ElMessage.info("此为目录节点，请在左侧树中展开下级；变量节点才可读取数值。");
        return;
    }
    try {
        const resp = await machineConnectionPointsApi.readTags(deviceId, {
            tags: [{ address: row.address, dataType: row.type, sourceId: row.sourceId }],
        });
        const r = resp.tags[0];
        if (!r) return;
        row.currentValue =
            r.errorMessage?.trim()
                ? `错误: ${r.errorMessage}`
                : stringifyTagValue(r.value);
    } catch (e: unknown) {
        const ax = e as { response?: { data?: { error?: string } } };
        ElMessage.error(ax.response?.data?.error ?? "点位读取失败");
    }
};

const handleWriteSinglePoint = async (row: PointRow) => {
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) return;
    if (!row.writable) {
        ElMessage.warning("该点位不可写");
        return;
    }
    const raw = String(row.currentValue ?? "").trim();
    if (!raw) {
        ElMessage.warning("请输入要写入的值");
        return;
    }
    let writeValue: unknown;
    try {
        writeValue = parseWriteValue(row.type, raw);
    } catch (err: unknown) {
        ElMessage.warning(err instanceof Error ? err.message : "写入值格式不正确");
        return;
    }
    try {
        const resp = await machineConnectionPointsApi.writeTags(deviceId, {
            tags: [{ address: row.address, dataType: row.type, value: writeValue }],
        });
        const r = resp.results[0];
        if (r?.success) {
            ElMessage.success("写入成功");
            await handleReadSinglePoint(row);
        } else {
            ElMessage.error(r?.errorMessage || "写入失败");
        }
    } catch (e: unknown) {
        if (e === "cancel") return;
        ElMessage.error(getApiErrorMessage(e, "点位写入失败"));
    }
};

const handlePointSelectionChange = (rows: PointRow[]) => {
    selectedPointRows.value = rows;

    // 需求：未勾选的点位频率默认为空；勾选后若无已保存值则回填默认 500
    const selected = new Set(rows.map((r) => r.id));
    const cfgMap = savedPointConfigByPath.value;
    for (const row of pointTableData.value) {
        const isSelected = selected.has(row.id);
        if (!isSelected) {
            row.frequency = "";
            continue;
        }
        if (row.nodeType === "Folder") {
            row.frequency = "";
            continue;
        }
        // 已勾选：优先用数据库回填
        const cfg = cfgMap.get(row.path);
        if (cfg?.collectionFrequency && Number.isFinite(cfg.collectionFrequency)) {
            row.frequency = String(cfg.collectionFrequency);
            continue;
        }
        // 无库值：给默认 500
        const n = Number(String(row.frequency ?? "").trim());
        if (!Number.isFinite(n) || n <= 0) {
            row.frequency = "500";
        }
    }
};

const handleSelectAllPoints = () => {
    const table = pointTableRef.value;
    if (!table) return;
    table.clearSelection();
    for (const row of filteredPointTableData.value) {
        table.toggleRowSelection(row, true);
    }
};

const handleInvertSelectPoints = () => {
    const table = pointTableRef.value;
    if (!table) return;
    const selected = new Set(selectedPointRows.value.map((r) => r.id));
    table.clearSelection();
    for (const row of filteredPointTableData.value) {
        if (!selected.has(row.id)) {
            table.toggleRowSelection(row, true);
        }
    }
};

const handleBatchReadPoints = async () => {
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) return;
    const targets = selectedPointRows.value.filter((r) => r.nodeType === "Variable");
    if (targets.length === 0) {
        ElMessage.warning("请先勾选至少一个变量点位（目录行不可批量读取）");
        return;
    }
    try {
        const resp = await machineConnectionPointsApi.readTags(deviceId, {
            tags: targets.map((t) => ({
                address: t.address,
                dataType: t.type,
                sourceId: t.sourceId,
            })),
        });
        const map = new Map(resp.tags.map((t) => [normalizeTagAddress(t.address), t]));
        for (const row of pointTableData.value) {
            const r = map.get(normalizeTagAddress(row.address));
            if (!r) continue;
            row.currentValue =
                r.errorMessage?.trim()
                    ? `错误: ${r.errorMessage}`
                    : stringifyTagValue(r.value);
        }
        ElMessage.success(`批量读取完成（${targets.length}项）`);
    } catch (e: unknown) {
        const ax = e as { response?: { data?: { error?: string } } };
        ElMessage.error(ax.response?.data?.error ?? "批量读取失败");
    }
};

const handleBatchWritePoints = async () => {
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) return;
    if (selectedPointRows.value.length === 0) {
        ElMessage.warning("请先勾选至少一个点位");
        return;
    }
    const targets = selectedPointRows.value.filter((r) => r.writable);
    if (targets.length === 0) {
        ElMessage.warning("勾选点位中没有可写项");
        return;
    }
    try {
        const result = await ElMessageBox.prompt(
            `将统一写入到 ${targets.length} 个点位`,
            "批量写入",
            {
                confirmButtonText: "写入",
                cancelButtonText: "取消",
                inputPlaceholder: "请输入统一写入值",
            },
        );
        const value = (result as { value: string }).value;
        const tags = targets.map((t) => ({
            address: t.address,
            dataType: t.type,
            value: parseWriteValue(t.type, value),
        }));
        const resp = await machineConnectionPointsApi.writeTags(deviceId, {
            tags,
        });
        const successCount = resp.results.filter((r) => r.success).length;
        const failCount = resp.results.length - successCount;
        if (successCount > 0) {
            await handleBatchReadPoints();
        }
        if (failCount === 0) {
            ElMessage.success(`批量写入成功（${successCount}项）`);
        } else {
            ElMessage.warning(`批量写入完成：成功${successCount}项，失败${failCount}项`);
        }
    } catch (e: unknown) {
        if (e === "cancel") return;
        ElMessage.error(getApiErrorMessage(e, "批量写入失败"));
    }
};

async function refreshSavedPathsFromDb(options: { silent?: boolean } = {}) {
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) return;
    const { silent = false } = options;
    try {
        const rows = await datacollectionApi.list(deviceId);
        savedPathsInDb.value = new Set(rows.map((r) => r.path));
        savedPointConfigByPath.value = new Map(
            rows.map((r) => [r.path, { collectionFrequency: r.collectionFrequency }]),
        );
        await applySavedPathsToTable();
    } catch (e: unknown) {
        const ax = e as { response?: { data?: { error?: string; detail?: string } } };
        const msg =
            ax.response?.data?.detail ??
            ax.response?.data?.error ??
            (e instanceof Error ? e.message : "加载已保存点位失败");
        console.error(e);
        if (!silent) {
            ElMessage.warning(String(msg));
        }
    }
}

async function applySavedPathsToTable() {
    await nextTick();
    const table = pointTableRef.value;
    if (!table) return;
    const pathSet = savedPathsInDb.value;
    const cfgMap = savedPointConfigByPath.value;
    table.clearSelection();
    for (const row of filteredPointTableData.value) {
        const cfg = cfgMap.get(row.path);
        if (cfg?.collectionFrequency && Number.isFinite(cfg.collectionFrequency)) {
            row.frequency = String(cfg.collectionFrequency);
        } else {
            // 未勾选默认空
            row.frequency = "";
        }
        if (pathSet.has(row.path)) {
            table.toggleRowSelection(row, true);
        }
    }
}

const handleSavePointConfig = async () => {
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) {
        ElMessage.warning("未找到设备信息，无法保存点位配置");
        return;
    }
    const selectedVars = selectedPointRows.value.filter((r) => r.nodeType === "Variable");
    if (selectedVars.length === 0) {
        ElMessage.warning("请勾选至少一个变量节点再保存（目录行不可采集）");
        return;
    }
    try {
        const visiblePaths = filteredPointTableData.value.map((row) => row.path);
        await datacollectionApi.sync({
            deviceId,
            items: selectedVars.map((row) => ({
                name: row.displayName,
                path: row.path,
                datatype: normalizeDataType(row.type || row.dataType),
                collectionFrequency: Number(String(row.frequency ?? "").trim() || 500),
                protocol: normalizeDatacollectionProtocol(pointDialogDeviceProtocol.value),
            })),
            visiblePaths,
        });
        savedCollectionPointsByDevice.value[deviceId] = selectedVars.map((row) => ({
            address: row.address,
            dataType: row.type,
            displayName: row.displayName,
            path: row.path,
            collectionFrequency: Number(String(row.frequency ?? "").trim() || 500),
        }));
        // 与当前列表勾选保持一致：先移除当前可见路径，再写入当前勾选路径
        const nextPaths = new Set(savedPathsInDb.value);
        for (const p of visiblePaths) {
            nextPaths.delete(p);
            savedPointConfigByPath.value.delete(p);
        }
        for (const r of selectedVars) {
            nextPaths.add(r.path);
        }
        savedPathsInDb.value = nextPaths;
        // 保存后同步回填缓存（避免再次切换节点又回到默认 500）
        for (const r of selectedVars) {
            const n = Number(String(r.frequency ?? "").trim() || 500);
            if (Number.isFinite(n) && n > 0) {
                savedPointConfigByPath.value.set(r.path, { collectionFrequency: n });
            }
        }
        ElMessage.success(`已同步保存，当前勾选 ${selectedVars.length} 个变量点位`);
    } catch (e: unknown) {
        ElMessage.error(getApiErrorMessage(e, "保存点位配置失败"));
    }
};

async function loadAddressChildren(
    parent: PointTreeNode,
    options: { silent?: boolean } = {},
) {
    const deviceId = pointDialogDeviceId.value;
    if (!deviceId) return;
    const { silent: _silent = false } = options;

    // parent.path 为 "/" 时，传 null/undefined 获取根节点
    const parentPath = parent.path === "/" ? undefined : parent.path;
    try {
        const rawNodes = await machineConnectionPointsApi.browseAddressSpace(
            deviceId,
            parentPath,
            pointDialogDeviceProtocol.value,
        );
        const nodes = sanitizeAddressSpaceLevelNodes(parentPath, rawNodes);
        parent.children = nodes.map(mapAddressNodeToTreeNode);
        parent._loaded = true;
    } catch (e: unknown) {
        parent.children = [];
        parent._loaded = true;
        // 点位加载失败时按需求静默处理，不提示 "Request failed with status code 500"
        void e;
        void _silent;
    }
}

/**
 * 浏览地址空间单层并映射为树节点（与左侧懒加载、右侧铺平共用一套过滤规则）
 */
async function browseAddressLevelAsPointTree(
    deviceId: string,
    parentPath: string | undefined,
): Promise<PointTreeNode[]> {
    const raw = await machineConnectionPointsApi.browseAddressSpace(
        deviceId,
        parentPath,
        pointDialogDeviceProtocol.value,
    );
    return sanitizeAddressSpaceLevelNodes(parentPath, raw).map(mapAddressNodeToTreeNode);
}

/**
 * 以某文件夹为根，广度优先逐层拉取子目录，收集所有 Variable 叶子（子目录下全部展平到列表）
 */
async function flattenAllDescendantVariableNodes(
    deviceId: string,
    folderPath: string,
): Promise<PointTreeNode[]> {
    const variables: PointTreeNode[] = [];
    const queue: string[] = [];
    const enqueued = new Set<string>();
    const failedFolders: string[] = [];
    const firstParent = folderPath === "/" ? undefined : folderPath;

    let first: PointTreeNode[] = [];
    try {
        first = await browseAddressLevelAsPointTree(deviceId, firstParent);
    } catch (e: unknown) {
        throw new Error(getApiErrorMessage(e, `加载目录 ${folderPath || "/"} 失败`));
    }
    for (const n of first) {
        if (n.nodeType === "Variable") {
            if (variables.length < POINT_FLATTEN_MAX_VARIABLES) {
                variables.push(n);
            }
        } else {
            queue.push(n.path);
            enqueued.add(n.path);
        }
    }

    let steps = 0;
    while (queue.length > 0
        && variables.length < POINT_FLATTEN_MAX_VARIABLES
        && steps < POINT_FLATTEN_MAX_FOLDER_STEPS) {
        const p = queue.shift()!;
        steps += 1;
        let level: PointTreeNode[];
        try {
            level = await browseAddressLevelAsPointTree(deviceId, p);
        } catch {
            // 子目录异常不应中断整棵树加载，记录后继续遍历其余分支。
            failedFolders.push(p);
            continue;
        }
        for (const n of level) {
            if (n.nodeType === "Variable") {
                if (variables.length < POINT_FLATTEN_MAX_VARIABLES) {
                    variables.push(n);
                }
            } else if (!enqueued.has(n.path)) {
                enqueued.add(n.path);
                queue.push(n.path);
            }
        }
    }

    if (failedFolders.length > 0) {
        const preview = failedFolders.slice(0, 3).join("、");
        ElMessage.warning(
            `部分子目录加载失败（${failedFolders.length} 个），已跳过：${preview}${failedFolders.length > 3 ? "…" : ""}`,
        );
    }

    return variables.sort((a, b) => a.path.localeCompare(b.path, undefined, { sensitivity: "base" }));
}

function collectFirstLevelExpandedKeys(root: PointTreeNode): string[] {
    const keys: string[] = [root.id];
    for (const child of root.children ?? []) {
        if (child.nodeType === "Folder") {
            keys.push(child.id);
        }
    }
    return keys;
}

async function expandPointTreeNodes(keys: string[]) {
    await nextTick();
    const tree = pointTreeRef.value;
    if (!tree) return;
    for (const key of keys) {
        const node = tree.getNode(key);
        if (node) node.expanded = true;
    }
}

function mapAddressNodeToTreeNode(n: AddressNode): PointTreeNode {
    const anyNode = n as unknown as Record<string, unknown>;
    const unit =
        String(
            anyNode.unit ??
            anyNode.Unit ??
            anyNode.engineeringUnit ??
            anyNode.EngineeringUnit ??
            "",
        ).trim() || undefined;
    const kind = normalizeAddressNodeToTreeKind(n);
    return {
        id: n.path,
        label: getAddressNodeLabel(n),
        nodeType: kind,
        path: n.path,
        dataType: n.dataType ?? undefined,
        unit,
        isReadable: !!n.isReadable,
        isWritable: !!n.isWritable,
        sourceId: n.sourceId ?? undefined,
        children: kind === "Folder" ? [] : undefined,
        _loaded: kind !== "Folder",
    };
}

function getAddressNodeLabel(n: Pick<AddressNode, "displayName" | "path">): string {
    const display = String(n.displayName ?? "").trim();
    if (display) {
        const normalized = display.replace(/\\/g, "/").replace(/\/+$/g, "");
        const leaf = normalized.split("/").filter(Boolean).pop();
        return (leaf && leaf.trim()) || display;
    }
    const path = String(n.path ?? "").trim().replace(/\\/g, "/").replace(/\/+$/g, "");
    const leaf = path.split("/").filter(Boolean).pop();
    return (leaf && leaf.trim()) || path || "/";
}

function mapVariableNodeToRow(n: PointTreeNode): PointRow {
    const dt = normalizeDataType(n.dataType);
    return {
        id: n.path,
        path: n.path,
        displayName: n.label,
        nodeType: n.nodeType,
        dataType: dt,
        isReadable: !!n.isReadable,
        isWritable: !!n.isWritable,
        children: n.children ? String(n.children.length) : "null",
        enabled: true,
        name: n.label,
        type: dt,
        address: n.path,
        sourceId: n.sourceId,
        currentValue: "-",
        unit: n.unit ?? "",
        // 未勾选时频率显示为空；勾选时若库里无值再回填默认 500
        frequency: "",
        multiplier: "1",
        desc: "",
        writable: !!n.isWritable,
    };
}

function normalizeDatacollectionProtocol(protocol: string) {
    const v = String(protocol ?? "").trim().toLowerCase();
    return v === "nclinkapi"
        ? "NCLinkApi"
        : "IndustrialIoT";
}

function normalizeDataType(dt?: string): string {
    const v = String(dt ?? "").trim();
    if (!v) return "Float";
    const noNamespace = v.includes(".") ? v.split(".").pop() ?? v : v;
    const normalized = noNamespace
        .trim()
        .replace(/,.*/g, "")
        .replace(/\[\]$/g, "")
        .replace(/`\d+$/g, "");
    // 后端 DataType 枚举大小写不敏感，但这里统一首字母大写
    const lower = normalized.toLowerCase();
    if (lower === "bool" || lower === "boolean" || lower === "bit" || lower === "sbool")
        return "Bool";
    if (lower === "int8" || lower === "sbyte" || lower === "sint")
        return "Int16";
    if (lower === "int16") return "Int16";
    if (lower === "int32" || lower === "int" || lower === "dint")
        return "Int32";
    if (lower === "int64" || lower === "long" || lower === "lint")
        return "Int64";
    if (lower === "uint8" || lower === "byte" || lower === "usint")
        return "UInt16";
    if (lower === "uint16" || lower === "word" || lower === "uint")
        return "UInt16";
    if (lower === "uint32" || lower === "dword" || lower === "udint")
        return "UInt32";
    if (lower === "float" || lower === "real" || lower === "sreal")
        return "Float";
    if (lower === "double" || lower === "lreal") return "Double";
    if (
        lower === "string" ||
        lower === "char" ||
        lower === "wchar" ||
        lower === "text" ||
        lower === "wstring"
    )
        return "String";
    if (lower === "bytearray") return "ByteArray";
    if (lower === "single") return "Float";
    if (lower === "ushort") return "UInt16";
    if (lower === "uint") return "UInt32";
    if (lower === "ulong") return "UInt32";
    if (lower === "short") return "Int16";
    if (lower === "long") return "Int64";
    if (lower === "integer") return "Int32";
    if (lower === "uinteger") return "UInt32";
    if (lower === "datetime" || lower === "guid" || lower === "localizedtext")
        return "String";
    // 兜底到 String，避免把后端不支持的类型名直接透传导致整批采集失败
    return "String";
}

/** 库表 datatype 存节点类型（如 Variable）时，读取采集需映射为协议数据类型 */
function mapDbDatatypeToReadType(dbDatatype: string): string {
    const t = String(dbDatatype ?? "").trim();
    // 兼容历史脏数据：旧版本把 nodeType 写入了 datatype（Variable/Folder）
    if (t === "Variable" || t === "Folder") return "String";
    return normalizeDataType(t);
}

function parseWriteValue(dataType: string, raw: string): unknown {
    const t = normalizeDataType(dataType);
    const s = raw.trim();
    if (s === "" || s === "-") {
        throw new Error("请输入有效写入值");
    }
    if (t === "Bool") {
        const v = s.toLowerCase();
        if (["true", "1", "yes", "y", "on"].includes(v)) return true;
        if (["false", "0", "no", "n", "off"].includes(v)) return false;
        throw new Error("布尔值仅支持：true/false/1/0");
    }
    if (t === "Int16" || t === "Int32" || t === "Int64" || t === "UInt16" || t === "UInt32") {
        const n = Number(s);
        if (!Number.isFinite(n) || !Number.isInteger(n)) {
            throw new Error("整数类型仅支持整数值");
        }
        return n;
    }
    if (t === "Float" || t === "Double") {
        const n = Number(s);
        if (!Number.isFinite(n)) {
            throw new Error("浮点类型仅支持数字值");
        }
        return n;
    }
    if (t === "ByteArray") {
        if (s.startsWith("[") && s.endsWith("]")) {
            try {
                return JSON.parse(s) as unknown;
            } catch {
                throw new Error("ByteArray 格式错误，应为如 [1,2,3] 的 JSON 数组");
            }
        }
        return s;
    }
    return s;
}

function getApiErrorMessage(e: unknown, fallback: string): string {
    const ax = e as {
        message?: string;
        response?: {
            data?:
            | string
            | {
                error?: string;
                detail?: string;
                message?: string;
                title?: string;
                errors?: Record<string, string[]>;
            };
        };
    };

    const data = ax.response?.data;
    if (typeof data === "string") {
        const s = data.trim();
        return s.length > 400 ? `${s.slice(0, 400)}…` : (s || ax.message?.trim() || fallback);
    }
    const modelErrors =
        data?.errors
            ? Object.values(data.errors)
                .flat()
                .filter(Boolean)
                .join("；")
            : "";
    const err = data?.error?.trim() ?? "";
    const detail = data?.detail?.trim() ?? "";
    if (err && detail && detail !== err) {
        return `${err}（${detail}）`;
    }
    return (
        err ||
        detail ||
        modelErrors.trim() ||
        data?.message?.trim() ||
        data?.title?.trim() ||
        ax.message?.trim() ||
        fallback
    );
}

function stringifyTagValue(v: unknown): string {
    if (v == null) return "-";
    if (typeof v === "string") return v;
    if (typeof v === "number" || typeof v === "boolean") return String(v);
    try {
        return JSON.stringify(v);
    } catch {
        return String(v);
    }
}

function normalizeTagAddress(a: string | null | undefined): string {
    return (a ?? "").trim().replace(/^\/+/, "");
}

function downloadBlob(blob: Blob, fileName: string) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
}

function escapeCsvField(value: unknown): string {
    const text = String(value ?? "");
    if (/[",\r\n]/.test(text)) {
        return `"${text.replace(/"/g, '""')}"`;
    }
    return text;
}

interface TransferHistoryRow {
    time: string;
    fileName: string;
    directionLabel: string;
    fileSizeLabel: string;
    durationLabel: string;
    throughputLabel: string;
    progress: string;
    status: string;
    statusType: "success" | "danger" | "warning" | "info";
    message: string;
}

const transferForm = ref({
    deviceId: "",
    direction: "upload" as "upload" | "download",
});
const transferRemotePath = ref("");
/** 最近一次通过路径选择器确认的远端类型：目录批量（仅 AddressSpace）或单文件 */
const transferRemotePathPickedKind = ref<"none" | "file" | "folder" | "batch">("none");
/** 上传所选本地文件（单选或多选） */
const transferSelectedFiles = ref<File[]>([]);
type TransferBatchSelection = {
    path: string;
    label: string;
    nodeType: "folder" | "file";
};
const transferBatchSelections = ref<TransferBatchSelection[]>([]);
const uploadFileInputRef = ref<HTMLInputElement>();
const remotePathPickerVisible = ref(false);
const remotePathExpandedKeys = ref<string[]>([]);
const remotePathSelectedNodeKey = ref("");
const remotePathDraft = ref("");
const remotePathDraftNodeType = ref<RemotePathNode["nodeType"] | "">("");
/** 地址空间批量下载：勾选统计说明（级联勾选由 el-tree 默认关联父子） */
const remotePathPickerCheckHint = ref("");
const transferHistory = ref<TransferHistoryRow[]>([]);
const transferHistoryLoading = ref(false);
const transferSubmitting = ref(false);

type RemotePathNode = {
    key: string;
    label: string;
    nodeType: "folder" | "file";
    /** 接口原始路径（用于后续 parentPath 递归请求） */
    path: string;
    /** 仅用于界面展示的可读路径，不参与请求 */
    displayPath?: string;
    /** lazy 模式下由 el-tree 读取，表示是否叶子，勿与空 children:[] 混用 */
    isLeaf?: boolean;
    children?: RemotePathNode[];
    _loaded?: boolean;
    _loading?: boolean;
};

const remotePathTreeRef = ref<InstanceType<typeof ElTree>>();
const remotePathTreeData = ref<RemotePathNode[]>([]);
/**
 * AddressSpace 懒加载时 el-tree 的 `data` 必须长期保持同一数组引用。
 * 若在模板里写 `[]`，每次重渲染都会得到新引用，触发 Element Plus 对 `data` 的 watch → `setData`，
 * 已懒加载展开的子树会被清空，出现点击节点后上方「暂无数据」、仅下方「已选路径」更新的现象。
 */
const remotePathPickerLazyTreeRootData = ref<RemotePathNode[]>([]);

const remotePathPickerLazyAddressSpace = computed(() => {
    const ui = devices.value.find((x) => x.id === transferForm.value.deviceId);
    return remotePathPickerUsesAddressSpace(ui);
});

const remotePathPickerShowCheckboxes = computed(() => {
    if (transferForm.value.direction !== "download") return false;
    const ui = devices.value.find((x) => x.id === transferForm.value.deviceId);
    if (ui?.protocol === "FOCAS" || ui?.protocol === "NCLinkApi") return false;
    return true;
});

const transferSelectedUploadLabel = computed(() => {
    const list = transferSelectedFiles.value;
    if (!list.length) return "未选择文件";
    const first = list[0];
    if (list.length === 1) return first?.name ?? "未选择文件";
    return `${list.length} 个文件（${first?.name ?? ""} 等）`;
});

/** 传输区：最近一次从选择器确认的路径类型标签 */
const transferRemotePathKindTag = computed(() => {
    if (!transferRemotePath.value.trim()) return "";
    if (transferRemotePathPickedKind.value === "batch") return "批量";
    if (transferRemotePathPickedKind.value === "folder") return "整目录";
    if (transferRemotePathPickedKind.value === "file") return "单节点";
    return "";
});

const transferBatchSelectionLabels = computed(() =>
    transferBatchSelections.value
        .filter((x) => x.nodeType === "file")
        .map((x) => x.label || x.path),
);

const transferBatchSelectionPreview = computed(() => {
    const labels = transferBatchSelectionLabels.value;
    if (!labels.length) return "未选择文件";
    const first = labels[0];
    if (labels.length === 1) return first;
    return `${first} 等 ${labels.length} 项`;
});

const selectedTransferDevice = computed(() =>
    devices.value.find((x) => x.id === transferForm.value.deviceId),
);

const transferChannelLabel = computed(() => {
    const ui = selectedTransferDevice.value;
    return ui?.transferProtocol || ui?.protocol || "未选择设备";
});

const transferChannelTagType = computed(() => {
    const protocol = selectedTransferDevice.value?.transferProtocol;
    return protocol === "FTP" || protocol === "SMB" ? "success" : "info";
});

const transferChannelDetail = computed(() => {
    const ui = selectedTransferDevice.value;
    if (!ui) return "请选择设备";
    if (ui.transferProtocol) {
        const auth = ui.transferUsername ? `，账号 ${ui.transferUsername}` : "";
        return `使用设备已保存的 ${ui.transferProtocol} 通道：${ui.transferHost}:${ui.transferPort}${auth}`;
    }
    return `未配置 FTP/SMB，当前会使用设备主协议 ${ui.protocol} 执行传输`;
});

/**
 * 当前设备尚未出现在 `devices` 中时，懒加载根节点会误判为「非 AddressSpace」并返回空数组。
 * 依赖此 key 在列表中出现该设备后强制重建 el-tree，重新触发根节点 load。
 */
const remotePathPickerTreeKey = computed(() => {
    const id = transferForm.value.deviceId ?? "";
    if (!remotePathPickerLazyAddressSpace.value) return `pt:${id}`;
    const ui = devices.value.find((x) => x.id === id);
    const hasDeviceRow = Boolean(ui);
    return `as:${id}:${hasDeviceRow ? "1" : "0"}:${devices.value.length}`;
});
const REMOTE_TREE_MAX_NODES = 2000;

const transferRemotePathRuleHint = computed(() => {
    const id = transferForm.value.deviceId;
    const ui = devices.value.find((x) => x.id === id);
    const upload = transferForm.value.direction === "upload";
    if (remotePathPickerUsesAddressSpace(ui)) {
        if (ui?.protocol === "FOCAS") {
            return upload
                ? "发那科（FOCAS）：与 Swagger 一致，按节点 path 作为 parentPath 逐层懒加载；上传请选可展开目录，Variable 为叶子"
                : "发那科（FOCAS）：单文件下载按地址空间接口返回 path（如 /CNC/...）";
        }
        return upload
            ? "西门子（OpcUa）：上传请选可展开的节点；子层用上一节点的 path 作为 parentPath 递归查询"
            : "西门子（OpcUa）：下载可勾选多项后点确定（批量 ZIP），或单选 Variable；上传可多选本地文件";
    }
    if (ui?.protocol === "NCLinkApi") {
        return upload
            ? "华中 NC-Link API：设备端路径填目录或完整 key；目录已存在时可填 selftest/，后端会拼接本地文件名"
            : "华中 NC-Link API：设备端路径填文件 key，如 O0001、Otemp、selftest/O99999";
    }
    if (ui?.protocol === "FOCAS") {
        return upload
            ? "发那科（FOCAS）：上传请选择接口返回的目录节点"
            : "发那科（FOCAS）：下载请使用接口返回的节点 path（如 /CNC/...）";
    }
    return upload ? "上传请选目录" : "下载请选文件";
});

function defaultRemotePathForDevice(ui: DeviceUi | undefined): string {
    if (!ui) return "";
    const u = ui.uploadPath?.trim();
    if (u) return u;
    // FOCAS：设备端路径统一通过 AddressSpace 接口返回的 path 选择
    if (ui.protocol === "FOCAS") return "";
    if (ui.protocol === "OpcUa") return "";
    return "";
}

function normalizeRemotePath(path: string): string {
    const p = String(path ?? "").trim();
    if (!p) return "";
    if (p.startsWith("//")) {
        const body = p.slice(2).replace(/\\/g, "/").replace(/\/+/g, "/");
        return `//${body}`;
    }
    return p.replace(/\\/g, "/").replace(/\/+/g, "/");
}

function getDeviceTransferRoot(ui: DeviceUi | undefined): string | undefined {
    if (!ui) return undefined;
    if (ui.protocol === "FOCAS") return "/";
    if (ui.protocol === "OpcUa") return "/";
    if (ui.protocol === "NCLinkApi") return undefined;
    return "/";
}

/** 设备端路径树：发那科 / 西门子均按 AddressSpace 返回的节点 path 作为下一层 parentPath 递归浏览 */
function remotePathPickerUsesAddressSpace(ui: DeviceUi | undefined): boolean {
    return ui?.protocol === "OpcUa" || ui?.protocol === "FOCAS";
}

function addressSpaceNodeIsFolder(n: AddressNode): boolean {
    const dtRaw = String(n.dataType ?? "").trim();
    const dt = dtRaw.toLowerCase();
    const hasRealDataType = dt.length > 0
        && dt !== "null"
        && dt !== "undefined"
        && dt !== "none"
        && dt !== "n/a"
        && dt !== "-";
    if (hasRealDataType) return false;
    const t = String(n.nodeType ?? "").trim().toLowerCase();
    if (t === "variable" || t === "property") return false;
    if (
        t === "folder"
        || t === "object"
        || t === "objectfolder"
        || t === "method"
    ) {
        return true;
    }
    // 未知类型按叶子处理，避免误展开（如接口把变量标成其它类型时出现无限 Position）
    return false;
}

/**
 * 将地址空间接口的 nodeType（C# 枚举数字、OPC UA 类型名等）统一为点位树用的 Folder / Variable。
 * 与严格字符串比较时，发那科/西门子等若返回 0/1 或 Object，若不做转换会导致「地址空间」下表格始终暂无数据。
 */
function normalizeAddressNodeToTreeKind(n: AddressNode): "Folder" | "Variable" {
    const raw = (n as unknown as { nodeType?: unknown }).nodeType;
    if (raw === 0 || raw === "0") return "Folder";
    if (raw === 1 || raw === "1") return "Variable";
    const s = String(raw ?? "").trim().toLowerCase();
    if (
        s === "folder"
        || s === "object"
        || s === "objectfolder"
        || s === "objecttype"
        || s === "method"
    ) {
        return "Folder";
    }
    if (s === "variable" || s === "property") return "Variable";
    return addressSpaceNodeIsFolder(n) ? "Folder" : "Variable";
}

function getDelimitedAddressSpaceRemainder(parentPath: string, childPath: string): string | null {
    for (const delimiter of [".", ":"]) {
        const prefix = `${parentPath}${delimiter}`;
        if (childPath.startsWith(prefix)) return childPath.slice(prefix.length);
    }
    return null;
}

/** 子节点 path 是否应出现在给定 parentPath 下列表（去重、去自引用、去非子孙） */
function isAddressSpaceChildPath(parentPath: string | undefined, childPath: string): boolean {
    const c = normalizeRemotePath(childPath);
    if (!c) return false;
    const pRaw = parentPath?.trim();
    if (!pRaw || pRaw === "/") {
        return true;
    }
    const p = normalizeRemotePath(pRaw).replace(/\/+$/, "") || pRaw;
    if (!p) return true;
    if (c === p) return false;
    if (p.startsWith("/") && !p.startsWith("//") && c.startsWith("/") && !c.startsWith("//")) {
        const prefix = p.endsWith("/") ? p : `${p}/`;
        return c.startsWith(prefix) || getDelimitedAddressSpaceRemainder(p, c) !== null;
    }
    return true;
}

/** 扁平地址表场景：仅保留相对 parent 多「一层路径段」的节点，避免整树摊在同一层或误嵌套。 */
function isImmediateAddressSpaceChild(
    parentPath: string | undefined,
    childPath: string,
): boolean {
    const c = normalizeRemotePath(childPath).replace(/\/+$/, "");
    if (!c) return false;

    const pTrim = parentPath?.trim();
    if (!pTrim || pTrim === "/") {
        if (c.startsWith("//")) {
            const body = c.slice(2);
            return body.split("/").filter(Boolean).length === 1;
        }
        if (c.startsWith("/")) {
            return c.split("/").filter(Boolean).length === 1;
        }
        return true;
    }

    const p = normalizeRemotePath(pTrim).replace(/\/+$/, "");
    if (c === p) return false;

    if (p.startsWith("//")) {
        if (!c.startsWith("//")) return false;
        const prefix = p.endsWith("/") ? p : `${p}/`;
        if (!c.startsWith(prefix)) return false;
        const rel = c.slice(prefix.length);
        return rel.length > 0 && !rel.includes("/");
    }

    if (p.startsWith("/")) {
        const atRel = c.startsWith(`${p}@`) ? c.slice(p.length + 1) : "";
        if (atRel.length > 0) return !atRel.includes("/");

        const prefix = p.endsWith("/") ? p : `${p}/`;
        const delimited = getDelimitedAddressSpaceRemainder(p, c);
        if (!c.startsWith(prefix)) return delimited !== null && !delimited.includes("/");
        const rel = c.slice(prefix.length);
        return rel.length > 0 && !rel.includes("/");
    }

    return true;
}

function sanitizeAddressSpaceLevelNodes(
    parentPath: string | undefined,
    nodes: AddressNode[],
): AddressNode[] {
    const seen = new Set<string>();
    const out: AddressNode[] = [];
    const allSlash = nodes.every((n) => {
        const t = String(n.path ?? "").trim();
        return !t || (t.startsWith("/") && !t.startsWith("//"));
    });
    for (const n of nodes) {
        const raw = String(n.path ?? "").trim();
        if (!raw) continue;
        const norm = raw.startsWith("//") ? normalizeRemotePath(raw) : normalizeRemotePath(raw);
        if (seen.has(norm)) continue;
        if (!isAddressSpaceChildPath(parentPath, norm)) continue;
        const pn = parentPath?.trim()
            ? normalizeRemotePath(parentPath.trim()).replace(/\/+$/, "")
            : "";
        const cn = norm.replace(/\/+$/, "");
        if (pn && cn === pn) continue;
        if (allSlash && !isImmediateAddressSpaceChild(parentPath, norm)) continue;
        seen.add(norm);
        out.push(n);
    }
    return out;
}

/** 地址空间懒加载树节点：文件夹不设 children，用 isLeaf=false 触发按需加载 */
function mapAddressNodeToRemotePathPickerNode(n: AddressNode): RemotePathNode {
    const raw = String(n.path ?? "").trim();
    const path = raw.startsWith("//") ? normalizeRemotePath(raw) : raw;
    const isFolder = addressSpaceNodeIsFolder(n);
    return {
        key: pathToRemoteNodeKey(path),
        label: getAddressNodeLabel(n),
        nodeType: isFolder ? "folder" : "file",
        path,
        isLeaf: !isFolder,
    };
}

/**
 * 按节点 path 作为 parentPath 请求子层；OpcUa 下若 path 无子节点（与部分 Swagger 手写 parentPath 不一致），
 * 再用 displayName 重试一次（如 path=ns=2;s=Sinumerik 无结果时试 Sinumerik）。
 */
async function browseAddressSpaceForPicker(
    deviceId: string,
    parent: RemotePathNode | null,
    ui: DeviceUi | undefined,
): Promise<AddressNode[]> {
    const p = parent?.path?.trim() ?? "";
    const parentPath =
        !parent || p === "" || p === "/" ? undefined : p;

    const primary = await machineConnectionPointsApi.browseAddressSpace(
        deviceId,
        parentPath,
        ui?.protocol,
    );
    if (primary.length > 0 || !parent || ui?.protocol !== "OpcUa") {
        return sanitizeAddressSpaceLevelNodes(parentPath, primary);
    }

    const candidates = buildOpcUaParentPathCandidates(parent, parentPath);
    for (const candidate of candidates) {
        if (candidate === parentPath) continue;
        const retry = await machineConnectionPointsApi.browseAddressSpace(
            deviceId,
            candidate,
            ui?.protocol,
        );
        if (retry.length > 0) {
            return sanitizeAddressSpaceLevelNodes(candidate, retry);
        }
    }
    return sanitizeAddressSpaceLevelNodes(parentPath, primary);
}

function buildOpcUaParentPathCandidates(
    parent: RemotePathNode,
    primaryPath: string | undefined,
): string[] {
    const out: string[] = [];
    const seen = new Set<string>();
    const push = (value?: string) => {
        const v = String(value ?? "").trim();
        if (!v || seen.has(v)) return;
        seen.add(v);
        out.push(v);
    };

    const rawPath = String(parent.path ?? "").trim();
    const displayPath = String(parent.displayPath ?? "").trim();
    const label = String(parent.label ?? "").trim();
    const base = rawPath || displayPath || label;
    const normalizedDisplay = displayPath ? normalizeRemotePath(displayPath) : "";

    push(primaryPath);
    push(rawPath);
    push(displayPath);
    push(normalizedDisplay);
    push(label);

    // 无前导 / 的片段再补一版 /xxx
    if (base && !base.startsWith("/") && !/^ns=\d+;/i.test(base) && !/^i=/i.test(base)) {
        push(`/${base}`);
    }

    // 尝试 OPC UA NodeId 风格 ns=2;s=...
    const nodeBody = normalizedDisplay || (base.startsWith("/") ? base : `/${base}`);
    if (nodeBody && !/^ns=\d+;s=/i.test(base)) {
        const bodyNoLeadingSlash = nodeBody.replace(/^\/+/, "");
        if (bodyNoLeadingSlash) push(`ns=2;s=${bodyNoLeadingSlash}`);
        push(`ns=2;s=${nodeBody}`);
    }

    return out;
}

async function loadRemotePathPickerLazy(
    node: { level: number; data: RemotePathNode },
    resolve: (data: RemotePathNode[]) => void,
) {
    const deviceId = transferForm.value.deviceId?.trim() ?? "";
    if (!deviceId) {
        ElMessage.warning("请先在传输窗口中选择设备");
        resolve([]);
        return;
    }
    const ui = devices.value.find((x) => x.id === deviceId);
    if (!ui) {
        ElMessage.warning("设备列表尚未包含当前设备，请点击「刷新列表」后再试");
        resolve([]);
        return;
    }
    if (!remotePathPickerUsesAddressSpace(ui)) {
        resolve([]);
        return;
    }

    const parent: RemotePathNode | null = node.level === 0 ? null : node.data;
    try {
        const addrNodes = await browseAddressSpaceForPicker(deviceId, parent, ui);
        resolve(addrNodes.map((n) => mapAddressNodeToRemotePathPickerNode(n)));
        syncRemotePathPickerCurrentNode();
    } catch (e) {
        ElMessage.error(getApiErrorMessage(e, "加载地址空间失败"));
        resolve([]);
    }
}

/**
 * 自根目录起按 AddressSpace 接口：每层用节点 path 作为 parentPath 拉子节点，收集非文件夹（如 Variable）叶子 path。
 */
async function collectAddressSpaceDownloadablePaths(
    deviceId: string,
    rootFolderPath: string,
    ui: DeviceUi | undefined,
): Promise<string[]> {
    const root = normalizeRemotePath(rootFolderPath);
    if (!root) return [];

    const leaves: string[] = [];
    const seenFolder = new Set<string>();
    const seenLeaf = new Set<string>();
    const queue: string[] = [root];

    let ops = 0;
    while (
        queue.length > 0
        && leaves.length < REMOTE_TREE_MAX_NODES
        && ops < REMOTE_TREE_MAX_NODES
    ) {
        ops++;
        const folder = queue.shift()!;
        if (seenFolder.has(folder)) continue;
        seenFolder.add(folder);

        const parent: RemotePathNode = {
            key: pathToRemoteNodeKey(folder),
            label: folder.split("/").filter(Boolean).pop() || folder,
            nodeType: "folder",
            path: folder,
        };

        let children: AddressNode[];
        try {
            children = await browseAddressSpaceForPicker(deviceId, parent, ui);
        } catch {
            continue;
        }

        for (const n of children) {
            const mapped = mapAddressNodeToRemotePathPickerNode(n);
            if (mapped.nodeType === "folder") {
                if (!seenFolder.has(mapped.path)) queue.push(mapped.path);
            } else if (!seenLeaf.has(mapped.path)) {
                seenLeaf.add(mapped.path);
                leaves.push(mapped.path);
            }
        }
    }
    return leaves;
}

function expandRemotePathTreeNode(data: RemotePathNode) {
    void nextTick(() => {
        const tree = remotePathTreeRef.value as unknown as {
            getNode?: (d: RemotePathNode | string) => { expand: () => void } | undefined;
        };
        tree?.getNode?.(data)?.expand?.();
    });
}

function mapTransferFileItemToNode(item: { name: string; path: string; nodeType: "folder" | "file" }): RemotePathNode {
    const path = normalizeRemotePath(item.path);
    return {
        key: pathToRemoteNodeKey(path),
        label: item.name || path.split("/").filter(Boolean).pop() || path,
        nodeType: item.nodeType,
        path,
        children: item.nodeType === "folder" ? [] : undefined,
        _loaded: false,
        _loading: false,
    };
}

function normalizeTransferPathByProtocol(path: string): string {
    const normalized = normalizeRemotePath(path);
    if (!normalized) return normalized;
    return normalized;
}

function normalizeNCLinkApiFilePath(path: string): string {
    return normalizeRemotePath(path).replace(/^\/+/, "");
}

function getRemotePathParent(path: string): string {
    const p = normalizeRemotePath(path);
    if (!p || p === "/") return "";
    if (p.startsWith("//")) {
        const body = p.slice(2);
        const parts = body.split("/").filter(Boolean);
        if (parts.length <= 1) return "";
        return `//${parts.slice(0, -1).join("/")}`;
    }
    if (!p.startsWith("/")) {
        const parts = p.split("/").filter(Boolean);
        if (parts.length <= 1) return "";
        return parts.slice(0, -1).join("/");
    }
    const parts = p.split("/").filter(Boolean);
    if (parts.length <= 1) return "/";
    return `/${parts.slice(0, -1).join("/")}`;
}

function createFolderNode(path: string): RemotePathNode {
    const normalized = normalizeRemotePath(path);
    const label =
        normalized.split("/").filter(Boolean).pop()
        || normalized.replace(/^\/+/, "")
        || "/";
    return {
        key: pathToRemoteNodeKey(normalized),
        label,
        nodeType: "folder",
        path: normalized,
        children: [],
        _loaded: true,
        _loading: false,
    };
}

function ensureFolderNode(
    path: string,
    nodeMap: Map<string, RemotePathNode>,
    rootNodes: RemotePathNode[],
): RemotePathNode {
    const normalized = normalizeRemotePath(path);
    const existing = nodeMap.get(normalized);
    if (existing) return existing;

    const node = createFolderNode(normalized);
    nodeMap.set(normalized, node);

    const parentPath = getRemotePathParent(normalized);
    if (!parentPath) {
        rootNodes.push(node);
    } else {
        const parentNode = ensureFolderNode(parentPath, nodeMap, rootNodes);
        if (!parentNode.children?.some((x) => x.path === node.path)) {
            parentNode.children = parentNode.children ?? [];
            parentNode.children.push(node);
        }
    }
    return node;
}

function buildRemotePathTree(
    items: { name: string; path: string; nodeType: "folder" | "file" }[],
): RemotePathNode[] {
    const rootNodes: RemotePathNode[] = [];
    const nodeMap = new Map<string, RemotePathNode>();
    const sorted = [...items].sort(
        (a, b) =>
            normalizeRemotePath(a.path).split("/").length
            - normalizeRemotePath(b.path).split("/").length,
    );

    for (const item of sorted) {
        const normalized = item.path.includes("/")
            ? normalizeTransferPathByProtocol(item.path)
            : normalizeNCLinkApiFilePath(item.path);
        if (!normalized) continue;
        const parentPath = getRemotePathParent(normalized);

        if (item.nodeType === "folder") {
            const folderNode = ensureFolderNode(normalized, nodeMap, rootNodes);
            if (item.name?.trim()) folderNode.label = item.name.trim();
            continue;
        }

        const fileNode = mapTransferFileItemToNode({
            name: item.name,
            path: normalized,
            nodeType: "file",
        });
        nodeMap.set(normalized, fileNode);
        if (!parentPath) {
            rootNodes.push(fileNode);
        } else {
            const parentNode = ensureFolderNode(parentPath, nodeMap, rootNodes);
            parentNode.children = parentNode.children ?? [];
            if (!parentNode.children.some((x) => x.path === fileNode.path)) {
                parentNode.children.push(fileNode);
            }
        }
    }

    const sortNodes = (nodes?: RemotePathNode[]) => {
        if (!nodes) return;
        nodes.sort((a, b) => {
            if (a.nodeType !== b.nodeType) return a.nodeType === "folder" ? -1 : 1;
            return a.label.localeCompare(b.label, "zh-CN");
        });
        for (const node of nodes) {
            if (node.children?.length) sortNodes(node.children);
        }
    };
    sortNodes(rootNodes);
    return rootNodes;
}

function getPathDepth(path: string): number {
    const p = normalizeRemotePath(path);
    if (!p) return 0;
    if (p.startsWith("//")) return p.slice(2).split("/").filter(Boolean).length;
    return p.split("/").filter(Boolean).length;
}

function hasDeepDescendants(
    items: { path: string }[],
    rootPath: string | undefined,
): boolean {
    if (!items.length) return false;
    if (!rootPath) return items.some((x) => getPathDepth(x.path) > 1);
    const rootDepth = getPathDepth(rootPath);
    return items.some((x) => getPathDepth(x.path) > rootDepth + 1);
}

async function fetchRemoteTreeItems(
    deviceId: string,
    rootPath: string | undefined,
): Promise<{ name: string; path: string; nodeType: "folder" | "file" }[]> {
    const first = await machineConnectionProgramTransferApi.files(
        deviceId,
        rootPath,
        true,
    );
    // 后端已正确递归时直接返回
    if (hasDeepDescendants(first, rootPath)) return first;

    // 兜底：若 recursive 未生效，前端逐层递归拉取
    const all = new Map<string, { name: string; path: string; nodeType: "folder" | "file" }>();
    const queue: string[] = [];
    const visited = new Set<string>();

    for (const item of first) {
        const fixedPath = normalizeTransferPathByProtocol(item.path);
        const key = `${item.nodeType}:${fixedPath}`;
        all.set(key, { ...item, path: fixedPath });
        if (item.nodeType === "folder") queue.push(fixedPath);
    }

    while (queue.length > 0 && all.size < REMOTE_TREE_MAX_NODES) {
        const folderPath = queue.shift()!;
        if (visited.has(folderPath)) continue;
        visited.add(folderPath);

        const children = await machineConnectionProgramTransferApi.files(
            deviceId,
            folderPath,
            false,
        );
        for (const child of children) {
            const fixedPath = normalizeTransferPathByProtocol(child.path);
            const key = `${child.nodeType}:${fixedPath}`;
            if (!all.has(key)) {
                all.set(key, { ...child, path: fixedPath });
                if (child.nodeType === "folder") queue.push(fixedPath);
            }
        }
    }
    return [...all.values()];
}

async function loadRemotePathChildren(parentNode?: RemotePathNode) {
    const deviceId = transferForm.value.deviceId;
    if (!deviceId) return;
    if (parentNode?.nodeType === "file") return;
    if (parentNode?._loading) return;
    if (parentNode?._loaded) return;

    const ui = devices.value.find((x) => x.id === deviceId);
    if (remotePathPickerUsesAddressSpace(ui)) return;

    if (parentNode) parentNode._loading = true;
    try {
        const rootPath = getDeviceTransferRoot(ui);
        const targetPath = parentNode?.path ?? rootPath;
        const items = await fetchRemoteTreeItems(deviceId, targetPath);
        const nodes = buildRemotePathTree(items);
        if (parentNode) {
            parentNode.children = nodes;
            parentNode._loaded = true;
        } else {
            remotePathTreeData.value = nodes;
            if (!nodes.length && ui?.protocol === "NCLinkApi") {
                ElMessage.info("未从设备读取到文件列表，可直接手工填写文件 key");
            }
        }
        syncRemotePathPickerCurrentNode();
    } catch (e) {
        ElMessage.error(getApiErrorMessage(e, "加载设备文件列表失败"));
        if (!parentNode) {
            remotePathTreeData.value = [];
        }
    } finally {
        if (parentNode) parentNode._loading = false;
    }
}

function mapProgramTransferToRow(r: ProgramTransferResponse): TransferHistoryRow {
    const directionLabel =
        r.direction === "Upload" ? "上传到设备" : "从设备下载";
    const progress =
        r.fileSize > 0
            ? `${Math.min(100, Math.round((r.bytesTransferred / r.fileSize) * 100))}%`
            : r.status === "Completed"
                ? "100%"
                : "-";
    let status: string;
    let statusType: TransferHistoryRow["statusType"];
    if (r.status === "Completed") {
        status = "成功";
        statusType = "success";
    } else if (r.status === "Failed") {
        status = "失败";
        statusType = "danger";
    } else if (r.status === "InProgress") {
        status = "进行中";
        statusType = "warning";
    } else {
        status = r.status;
        statusType = "info";
    }
    const message =
        r.errorMessage?.trim()
        || (r.checksum
            ? `校验 ${r.checksum.length > 20 ? `${r.checksum.slice(0, 20)}…` : r.checksum}`
            : "")
        || (r.bytesTransferred > 0 ? `已传 ${r.bytesTransferred} B` : "-");
    const durationLabel = formatDuration(r.durationMs);
    const throughputLabel = formatTransferThroughput(
        r.bytesTransferred > 0 ? r.bytesTransferred : r.fileSize,
        r.durationMs,
    );

    return {
        time: formatSeenAt(r.startedAt),
        fileName: r.fileName,
        directionLabel,
        fileSizeLabel: formatFileSize(r.fileSize),
        durationLabel,
        throughputLabel,
        progress,
        status,
        statusType,
        message,
    };
}

function formatFileSize(bytes: number | null | undefined): string {
    const n = Number(bytes ?? 0);
    if (!Number.isFinite(n) || n <= 0) return "-";
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(2)} KB`;
    if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(2)} MB`;
    return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function formatDuration(durationMs: number | null | undefined): string {
    const n = Number(durationMs ?? 0);
    if (!Number.isFinite(n) || n <= 0) return "-";
    if (n < 1000) return `${Math.round(n)} ms`;
    const seconds = n / 1000;
    if (seconds < 60) return `${seconds.toFixed(2)} s`;
    const mins = Math.floor(seconds / 60);
    const sec = Math.round(seconds % 60);
    return `${mins}m ${sec}s`;
}

function formatTransferThroughput(
    bytes: number | null | undefined,
    durationMs: number | null | undefined,
): string {
    const b = Number(bytes ?? 0);
    const t = Number(durationMs ?? 0);
    if (!Number.isFinite(b) || !Number.isFinite(t) || b <= 0 || t <= 0) return "-";
    const bytePerSec = b / (t / 1000);
    if (bytePerSec < 1024) return `${bytePerSec.toFixed(2)} B/s`;
    if (bytePerSec < 1024 * 1024) return `${(bytePerSec / 1024).toFixed(2)} KB/s`;
    return `${(bytePerSec / (1024 * 1024)).toFixed(2)} MB/s`;
}

async function loadTransferHistory(deviceId: string) {
    if (!deviceId) {
        transferHistory.value = [];
        return;
    }
    transferHistoryLoading.value = true;
    try {
        const list = await machineConnectionProgramTransferApi.history(deviceId);
        const sorted = [...list].sort(
            (a, b) =>
                new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime(),
        );
        transferHistory.value = sorted.map(mapProgramTransferToRow);
    } catch (e) {
        transferHistory.value = [];
        ElMessage.error(getApiErrorMessage(e, "加载传输历史失败"));
    } finally {
        transferHistoryLoading.value = false;
    }
}

const openTransferDialog = (device: { id: string; name: string }) => {
    transferDialogDeviceName.value = device.name;
    transferForm.value.deviceId = device.id;
    const ui = devices.value.find((x) => x.id === device.id);
    transferRemotePath.value = defaultRemotePathForDevice(ui);
    transferRemotePathPickedKind.value = "none";
    transferBatchSelections.value = [];
    transferSelectedFiles.value = [];
    transferDialogVisible.value = true;
    void loadTransferHistory(device.id);
};

watch(
    () => transferForm.value.deviceId,
    (id) => {
        if (transferDialogVisible.value && id) {
            const ui = devices.value.find((x) => x.id === id);
            transferRemotePath.value = defaultRemotePathForDevice(ui);
            transferRemotePathPickedKind.value = "none";
            transferBatchSelections.value = [];
            transferSelectedFiles.value = [];
            void loadTransferHistory(id);
        }
    },
);

const startTransfer = async () => {
    const deviceId = transferForm.value.deviceId;
    if (!deviceId) {
        ElMessage.warning("请选择设备");
        return;
    }
    const ui = devices.value.find((x) => x.id === deviceId);
    let remotePath = transferRemotePath.value.trim();
    if (ui?.protocol === "NCLinkApi") {
        remotePath = normalizeNCLinkApiFilePath(remotePath);
    }
    const isFocas = ui?.protocol === "FOCAS";
    const isBatchAddressDownload =
        !isFocas
        && transferForm.value.direction === "download"
        && transferRemotePathPickedKind.value === "batch"
        && transferBatchSelections.value.length > 0;
    if (!remotePath && !isBatchAddressDownload) {
        ElMessage.warning(
            "请填写设备端路径（可从「选择设备文件」按 path 递归选定；以接口返回数据为准）",
        );
        return;
    }
    if (!isBatchAddressDownload && isFocas) {
        const addrSpace = remotePath.startsWith("/") && !remotePath.startsWith("//");
        if (!addrSpace) {
            ElMessage.warning("发那科（FOCAS）请使用接口返回的 path（如 /CNC/...）");
            return;
        }
    }
    if (!isBatchAddressDownload && ui?.protocol === "OpcUa") {
        const ftpLike = remotePath.startsWith("/") && !remotePath.startsWith("//");
        const opcNodeId =
            /^i=/i.test(remotePath)
            || /^ns=\d+;/i.test(remotePath);
        if (!ftpLike && !opcNodeId) {
            ElMessage.warning(
                "西门子（OpcUa）请使用以 / 开头的 FTP 路径，或地址空间中节点的 NodeId（如 i=2253、ns=2;s=Sinumerik）",
            );
            return;
        }
    }
    if (transferForm.value.direction === "upload" && transferSelectedFiles.value.length === 0) {
        ElMessage.warning("请选择要上传的文件（可多选）");
        return;
    }

    transferSubmitting.value = true;
    try {
        if (transferForm.value.direction === "upload") {
            const files = transferSelectedFiles.value;
            if (isFocas && files.length > 1) {
                ElMessage.warning("发那科（FOCAS）上传仅支持单文件");
                return;
            }
            if (files.length > 1) {
                const task = await machineConnectionProgramTransferApi.uploadBatchWait(
                    deviceId,
                    remotePath,
                    files,
                );
                const failed = task.failedFiles ?? 0;
                if (failed > 0) {
                    ElMessage.warning(
                        `批量上传结束：成功 ${task.completedFiles ?? 0}，失败 ${failed}`,
                    );
                } else {
                    ElMessage.success(
                        `批量上传已完成（${task.totalFiles ?? files.length} 个文件）`,
                    );
                }
            } else {
                const res = await machineConnectionProgramTransferApi.upload(
                    deviceId,
                    files[0]!,
                    remotePath,
                );
                if (res.status === "Failed") {
                    ElMessage.error(res.errorMessage?.trim() || "上传失败");
                } else {
                    ElMessage.success("上传已完成");
                }
            }
        } else if (isBatchAddressDownload) {
            const selectedFolders = transferBatchSelections.value.filter((x) => x.nodeType === "folder");
            const selectedFiles = transferBatchSelections.value.filter((x) => x.nodeType === "file");
            if (!selectedFolders.length && !selectedFiles.length) {
                ElMessage.warning("请先选择要下载的设备路径");
                return;
            }
            const out = new Set<string>();
            for (const item of selectedFiles) {
                if (item.path?.trim()) out.add(item.path.trim());
            }
            for (const item of selectedFolders) {
                const files = await machineConnectionProgramTransferApi.files(
                    deviceId,
                    item.path,
                    true,
                );
                for (const file of files) {
                    if (file.nodeType === "file" && file.path?.trim()) {
                        out.add(file.path.trim());
                    }
                }
            }
            const candidates = [...out];
            const paths = candidates.filter((p) => isDownloadableProgramPathForProtocol(p, ui));
            const skipped = candidates.length - paths.length;
            if (skipped > 0) {
                ElMessage.warning(`已忽略 ${skipped} 个非程序文件路径（如地址点位）`);
            }
            if (!paths.length) {
                ElMessage.warning("所选路径下没有可下载的程序文件");
            } else {
                await machineConnectionProgramTransferApi.downloadBatchZip(deviceId, paths);
                ElMessage.success(`已批量下载 ${paths.length} 个程序文件并打包为 ZIP`);
            }
        } else if (
            transferRemotePathPickedKind.value === "folder"
            && remotePathPickerUsesAddressSpace(ui)
        ) {
            ElMessage.info("正在按 path 递归收集可下载节点…");
            const leaves = await collectAddressSpaceDownloadablePaths(
                deviceId,
                remotePath,
                ui,
            );
            if (!leaves.length) {
                ElMessage.warning("该目录下没有可下载的叶子节点（如 Variable）");
            } else {
                await machineConnectionProgramTransferApi.downloadBatchZip(
                    deviceId,
                    leaves,
                );
                ElMessage.success(
                    `已批量下载 ${leaves.length} 个节点并打包为 ZIP`,
                );
            }
        } else {
            await machineConnectionProgramTransferApi.download(deviceId, remotePath);
            ElMessage.success("下载已保存到本地");
        }
        await loadTransferHistory(deviceId);
    } catch (e) {
        ElMessage.error(getApiErrorMessage(e, "传输失败"));
    } finally {
        transferSubmitting.value = false;
    }
};

const triggerSelectUploadPath = () => {
    uploadFileInputRef.value?.click();
};

const openRemotePathPicker = () => {
    if (!transferForm.value.deviceId?.trim()) {
        ElMessage.warning("请先在传输窗口中选择设备");
        return;
    }
    remotePathDraft.value = transferRemotePath.value;
    remotePathSelectedNodeKey.value = pathToRemoteNodeKey(remotePathDraft.value);
    const rememberedPaths = transferBatchSelections.value.map((x) => x.path);
    const seedPaths = rememberedPaths.length
        ? rememberedPaths
        : (transferRemotePath.value.trim() ? [transferRemotePath.value.trim()] : []);
    const expanded = new Set<string>();
    for (const p of seedPaths) {
        for (const k of buildRemoteNodeAncestorKeys(p)) expanded.add(k);
    }
    remotePathExpandedKeys.value = [...expanded];
    remotePathDraftNodeType.value = "";
    remotePathPickerVisible.value = true;
    const ui = devices.value.find((x) => x.id === transferForm.value.deviceId);
    if (!remotePathPickerUsesAddressSpace(ui)) {
        void loadRemotePathChildren();
    }
    remotePathPickerCheckHint.value = "";
    void nextTick(() => {
        const tree = remotePathTreeRef.value as unknown as {
            setCheckedKeys?: (keys: string[]) => void;
            setCurrentKey?: (key?: string, shouldAutoExpandParent?: boolean) => void;
        };
        const rememberedKeys = transferBatchSelections.value.map((x) => pathToRemoteNodeKey(x.path));
        const fallbackSingleFileKey =
            transferRemotePathPickedKind.value === "file" && transferRemotePath.value.trim()
                ? [pathToRemoteNodeKey(transferRemotePath.value.trim())]
                : [];
        tree?.setCheckedKeys?.(rememberedKeys.length ? rememberedKeys : fallbackSingleFileKey);
        if (remotePathSelectedNodeKey.value) {
            tree?.setCurrentKey?.(remotePathSelectedNodeKey.value, true);
        }
        refreshRemotePathPickerCheckSummary();
    });
};

function syncRemotePathPickerCurrentNode() {
    if (!remotePathPickerVisible.value) return;
    const key = remotePathSelectedNodeKey.value?.trim();
    if (!key) return;
    void nextTick(() => {
        const tree = remotePathTreeRef.value as unknown as {
            setCurrentKey?: (key?: string, shouldAutoExpandParent?: boolean) => void;
        };
        tree?.setCurrentKey?.(key, true);
    });
}

function refreshRemotePathPickerCheckSummary() {
    if (!remotePathPickerVisible.value || !remotePathPickerShowCheckboxes.value) {
        remotePathPickerCheckHint.value = "";
        return;
    }
    const tree = remotePathTreeRef.value as unknown as {
        getCheckedNodes?: (leafOnly?: boolean, includeHalfChecked?: boolean) => unknown[];
    };
    const raw = (tree?.getCheckedNodes?.(false, false) ?? []) as RemotePathNode[];
    const folders = raw.filter((n) => n?.nodeType === "folder");
    const files = raw.filter((n) => n?.nodeType === "file");
    const segs: string[] = [];
    if (files.length) segs.push(`变量（文件）${files.length} 个`);
    if (folders.length) {
        segs.push(
            `文件夹 ${folders.length} 个（勾选父级会级联已加载子节点；批量下载时文件夹会递归其下全部可下载叶子）`,
        );
    }
    remotePathPickerCheckHint.value = segs.length ? segs.join("；") : "未勾选下载项";
}

function onRemotePathPickerCheckChange() {
    void nextTick(() => refreshRemotePathPickerCheckSummary());
}

const handleRemotePathNodeClick = (node: RemotePathNode) => {
    const ui = devices.value.find((x) => x.id === transferForm.value.deviceId);
    const lazyAddr = remotePathPickerUsesAddressSpace(ui);
    const isUpload = transferForm.value.direction === "upload";
    if (isUpload && node.nodeType !== "folder") {
        ElMessage.warning("上传请选择目录");
        return;
    }
    if (!isUpload && node.nodeType !== "file") {
        if (lazyAddr) {
            remotePathDraft.value = node.path;
            remotePathSelectedNodeKey.value = node.key;
            remotePathDraftNodeType.value = node.nodeType;
            if (node.nodeType === "folder") {
                if (!remotePathExpandedKeys.value.includes(node.key)) {
                    remotePathExpandedKeys.value.push(node.key);
                }
                expandRemotePathTreeNode(node);
            }
            return;
        }
        ElMessage.warning("下载请选择文件");
        if (node.nodeType === "folder") {
            void loadRemotePathChildren(node);
        }
        return;
    }
    remotePathDraft.value = node.path;
    remotePathSelectedNodeKey.value = node.key;
    remotePathDraftNodeType.value = node.nodeType;
    if (node.nodeType === "folder") {
        if (!remotePathExpandedKeys.value.includes(node.key)) {
            remotePathExpandedKeys.value.push(node.key);
        }
        if (lazyAddr) expandRemotePathTreeNode(node);
        else void loadRemotePathChildren(node);
    }
};

const confirmRemotePath = () => {
    if (remotePathPickerShowCheckboxes.value) {
        const tree = remotePathTreeRef.value as unknown as {
            getCheckedNodes?: (leafOnly?: boolean, includeHalfChecked?: boolean) => unknown[];
        };
        const raw = (tree?.getCheckedNodes?.(false, false) ?? []) as RemotePathNode[];
        if (!raw.length) {
            ElMessage.warning("请先勾选要下载的文件或文件夹");
            return;
        }

        const checked = raw
            .filter((x) => x?.path?.trim())
            .map((x) => ({
                path: x.path.trim(),
                label: x.label?.trim() || x.path.trim(),
                nodeType: x.nodeType,
            }));
        const files = checked.filter((x) => x.nodeType === "file");
        const folders = checked.filter((x) => x.nodeType === "folder");

        // 单文件优先：只勾选 1 个文件且无文件夹时，按单文件下载。
        if (files.length === 1 && folders.length === 0) {
            transferBatchSelections.value = [files[0]!];
            transferRemotePath.value = files[0]!.path;
            transferRemotePathPickedKind.value = "file";
            remotePathPickerVisible.value = false;
            return;
        }

        // 多选场景：文件与文件夹都可参与批量下载。
        transferBatchSelections.value = [...folders, ...files];
        if (!transferBatchSelections.value.length) {
            ElMessage.warning("请先勾选要下载的设备路径");
            return;
        }
        transferRemotePath.value = `已勾选 ${files.length} 个文件`;
        transferRemotePathPickedKind.value = "batch";
        remotePathPickerVisible.value = false;
        return;
    }

    if (!remotePathDraft.value) {
        ElMessage.warning("请先选择设备端路径");
        return;
    }
    if (transferForm.value.direction === "upload" && remotePathDraftNodeType.value === "file") {
        ElMessage.warning("上传请选择目录");
        return;
    }
    const ui = devices.value.find((x) => x.id === transferForm.value.deviceId);
    const addrPicker = remotePathPickerUsesAddressSpace(ui);
    if (
        transferForm.value.direction === "download"
        && remotePathDraftNodeType.value === "folder"
        && !addrPicker
    ) {
        ElMessage.warning("下载请选择文件");
        return;
    }
    transferRemotePath.value = remotePathDraft.value;
    transferBatchSelections.value = [];
    if (transferForm.value.direction === "upload") {
        transferRemotePathPickedKind.value = "folder";
    } else {
        transferRemotePathPickedKind.value =
            remotePathDraftNodeType.value === "folder" ? "folder" : "file";
    }
    remotePathPickerVisible.value = false;
};

function pathToRemoteNodeKey(path: string): string {
    return String(path ?? "").trim().replace(/^\/\//, "").replace(/\\/g, "/");
}

function isDownloadableProgramPathForProtocol(path: string, ui?: DeviceUi): boolean {
    const p = normalizeRemotePath(path);
    if (!p) return false;
    if (!ui) return true;
    if (ui.protocol === "NCLinkApi") {
        return !p.startsWith("/");
    }
    if (ui.protocol === "FOCAS") {
        // FOCAS：仅按地址空间接口返回的 path 下载（/CNC/...）。
        return p.startsWith("/") && !p.startsWith("//");
    }
    return true;
}

function buildRemoteNodeAncestorKeys(path: string): string[] {
    const p = normalizeRemotePath(path);
    if (!p) return [];
    const out: string[] = [];

    if (p.startsWith("//")) {
        const parts = p.slice(2).split("/").filter(Boolean);
        let current = "//";
        for (let i = 0; i < parts.length - 1; i++) {
            current += i === 0 ? parts[i] : `/${parts[i]}`;
            out.push(pathToRemoteNodeKey(current));
        }
        return out;
    }

    if (p.startsWith("/")) {
        const parts = p.split("/").filter(Boolean);
        let current = "";
        for (let i = 0; i < parts.length - 1; i++) {
            current += `/${parts[i]}`;
            out.push(pathToRemoteNodeKey(current));
        }
        return out;
    }

    return out;
}

const handleUploadFilesChange = (event: Event) => {
    const input = event.target as HTMLInputElement;
    const list = input.files ? Array.from(input.files) : [];
    input.value = "";
    if (!list.length) return;
    transferSelectedFiles.value = list;
};

function onTransferRemotePathInput() {
    transferBatchSelections.value = [];
    transferRemotePathPickedKind.value = transferRemotePath.value.trim() ? "none" : "none";
}

// 弹窗数据
const dialogVisible = ref(false);
const dialogTitle = ref("新增设备");
const detailDialogVisible = ref(false);
const detailDevice = ref<DeviceUi | null>(null);

const detailMqttSummary = computed(() => {
    const d = detailDevice.value;
    if (!d || d.protocol !== "NCLink") return "-";
    const h = d.mqttBrokerHost?.trim();
    const p = d.mqttBrokerPort?.trim();
    if (h && p) return `${h} : ${p}`;
    if (h) return h;
    if (p) return `端口 ${p}`;
    return "（未单独配置，通常与主机/端口一致）";
});

function treeDefaultsForNewDevice(): Record<string, unknown> {
    const nodeId = selectedTreeNodeId.value;
    const key = nodeId.startsWith("brand-") ? nodeId.slice("brand-".length) : "";
    const brandLabel = key ? BRAND_KEY_TO_FORM_LABEL[key] : null;

    if (nodeId === "brand-siemens") {
        return {
            brand: brandLabel ?? "西门子",
            protocol: "OpcUa",
            port: 4840,
            useSecurity: "false",
            autoAcceptUntrustedCerts: "true",
            rejectSHA1SignedCertificates: "false",
            suppressNonceValidationErrors: "true",
            endpointUrl: "",
            transferProtocol: "FTP",
            transferPort: 21,
            transferConnectTimeoutMs: 10000,
            transferReadTimeoutMs: 5000,
        };
    }
    if (nodeId === "brand-fanuc") {
        return {
            brand: brandLabel ?? "法那科",
            protocol: "FOCAS",
            port: 8193,
            axisLabels: "X,Y,Z,A,B,C",
        };
    }
    if (nodeId === "brand-huazhong") {
        return {
            brand: brandLabel ?? "华中数控",
            protocol: "OpcUa",
            port: 4840,
            nclinkBrand: "华中数控",
            useSecurity: "false",
            autoAcceptUntrustedCerts: "true",
        };
    }
    if (brandLabel) {
        return { brand: brandLabel };
    }
    return {};
}

const deviceForm = ref({
    id: "",
    name: "",
    deviceType: "CNC" as DeviceTypeApi,
    brand: "法那科",
    code: "",
    model: "0i-MF",
    line: "",
    ip: "127.0.0.1",
    port: 8193,
    protocol: "FOCAS",
    station: 1,
    baudRate: 9600,
    connectTimeoutMs: 10000,
    readTimeoutMs: 5000,
    axisLabels: "X,Y,Z,A,B,C",
    uploadPath: "",
    authType: "anonymous",
    username: "",
    password: "",
    opcuaSecurityPolicy: "None",
    opcuaSecurityMode: "None",
    opcuaNamespaceIndex: 0,
    opcuaServerUri: "",
    useSecurity: "false",
    autoAcceptUntrustedCerts: "true",
    rejectSHA1SignedCertificates: "false",
    suppressNonceValidationErrors: "true",
    endpointUrl: "",
    transferProtocol: "",
    transferHost: "",
    transferPort: 21,
    transferUsername: "",
    transferPassword: "",
    transferShareName: "",
    transferConnectTimeoutMs: 10000,
    transferReadTimeoutMs: 5000,
    deviceGuid: "",
    nclinkBrand: "华中数控",
    mqttBrokerHost: "",
    mqttBrokerPort: "",
    mqttUsername: "",
    mqttPassword: "",
    ncLinkApiDeviceId: "",
    ncLinkApiBaseUrl: "",
    gskDeviceSn: "",
    gskScheme: "http",
    gskManagementBaseUrl: "",
    gskWorkshopAuthToken: "",
});

watch(
    () => deviceForm.value.transferProtocol,
    (protocol) => {
        if (protocol === "FTP" && (!deviceForm.value.transferPort || deviceForm.value.transferPort === 445)) {
            deviceForm.value.transferPort = 21;
        }
        if (protocol === "SMB" && (!deviceForm.value.transferPort || deviceForm.value.transferPort === 21)) {
            deviceForm.value.transferPort = 445;
        }
    },
);

// 打开新增设备弹窗
const openAddDeviceDialog = () => {
    dialogTitle.value = "新增设备";
    const fromTree = treeDefaultsForNewDevice();
    deviceForm.value = {
        id: "",
        name: "",
        deviceType: "CNC",
        brand: "法那科",
        code: "",
        model: "0i-MF",
        line: "",
        ip: "127.0.0.1",
        port: 8193,
        protocol: "FOCAS",
        station: 1,
        baudRate: 9600,
        connectTimeoutMs: 10000,
        readTimeoutMs: 5000,
        axisLabels: "X,Y,Z,A,B,C",
        uploadPath: "",
        authType: "anonymous",
        username: "",
        password: "",
        opcuaSecurityPolicy: "None",
        opcuaSecurityMode: "None",
        opcuaNamespaceIndex: 0,
        opcuaServerUri: "",
        useSecurity: "false",
        autoAcceptUntrustedCerts: "true",
        rejectSHA1SignedCertificates: "false",
        suppressNonceValidationErrors: "true",
        endpointUrl: "",
        transferProtocol: "",
        transferHost: "",
        transferPort: 21,
        transferUsername: "",
        transferPassword: "",
        transferShareName: "",
        transferConnectTimeoutMs: 10000,
        transferReadTimeoutMs: 5000,
        deviceGuid: "",
        nclinkBrand: "华中数控",
        mqttBrokerHost: "",
        mqttBrokerPort: "",
        mqttUsername: "",
        mqttPassword: "",
        ncLinkApiDeviceId: "",
        ncLinkApiBaseUrl: "",
        gskDeviceSn: "",
        gskScheme: "http",
        gskManagementBaseUrl: "",
        gskWorkshopAuthToken: "",
        ...fromTree,
    } as typeof deviceForm.value;
    dialogVisible.value = true;
};

// 编辑设备
const editDevice = (device: DeviceUi) => {
    dialogTitle.value = "编辑设备";
    deviceForm.value = {
        id: device.id,
        name: device.name,
        deviceType: device.deviceType,
        brand: device.brand,
        code: device.code,
        model: device.model,
        line: device.line,
        ip: device.ip,
        port: device.port,
        protocol: device.protocol,
        station: device.station,
        baudRate: device.baudRate,
        connectTimeoutMs: device.connectTimeoutMs,
        readTimeoutMs: device.readTimeoutMs,
        axisLabels: device.axisLabels,
        uploadPath: device.uploadPath ?? "",
        authType: device.authType,
        username: device.username ?? "",
        password: "",
        opcuaSecurityPolicy: device.opcuaSecurityPolicy ?? "None",
        opcuaSecurityMode: device.opcuaSecurityMode ?? "None",
        opcuaNamespaceIndex: device.opcuaNamespaceIndex ?? 0,
        opcuaServerUri: device.opcuaServerUri ?? "",
        useSecurity: device.useSecurity || "false",
        autoAcceptUntrustedCerts: device.autoAcceptUntrustedCerts || "true",
        rejectSHA1SignedCertificates: device.rejectSHA1SignedCertificates || "false",
        suppressNonceValidationErrors: device.suppressNonceValidationErrors || "true",
        endpointUrl: device.endpointUrl || "",
        transferProtocol: device.transferProtocol || "",
        transferHost: device.transferHost || "",
        transferPort: device.transferPort || 21,
        transferUsername: device.transferUsername || "",
        transferPassword: "",
        transferShareName: device.transferShareName || "",
        transferConnectTimeoutMs: device.transferConnectTimeoutMs || 10000,
        transferReadTimeoutMs: device.transferReadTimeoutMs || 5000,
        deviceGuid: device.deviceGuid ?? "",
        nclinkBrand: device.nclinkBrand || "华中数控",
        mqttBrokerHost: device.mqttBrokerHost ?? "",
        mqttBrokerPort: device.mqttBrokerPort ?? "",
        mqttUsername: device.mqttUsername ?? "",
        mqttPassword: "",
        ncLinkApiDeviceId: String(device.extendedProperties?.DeviceId ?? ""),
        ncLinkApiBaseUrl: String(device.extendedProperties?.ApiBaseUrl ?? ""),
        gskDeviceSn: String(device.extendedProperties?.DeviceSn ?? ""),
        gskScheme: String(device.extendedProperties?.Scheme ?? "http"),
        gskManagementBaseUrl: String(device.extendedProperties?.ManagementBaseUrl ?? ""),
        gskWorkshopAuthToken: String(device.extendedProperties?.WorkshopAuthToken ?? ""),
    };
    dialogVisible.value = true;
};

// 保存设备
const saveDevice = async () => {
    const f = deviceForm.value;
    if (!f.name?.trim()) {
        ElMessage.warning("请输入设备名称");
        return;
    }
    if (!String(f.code ?? "").trim()) {
        ElMessage.warning("请输入设备编号");
        return;
    }
    if (!f.brand?.trim()) {
        ElMessage.warning("请输入品牌 brand");
        return;
    }
    if (!f.model?.trim()) {
        ElMessage.warning("请输入型号 model");
        return;
    }
    if (!f.protocol) {
        ElMessage.warning("请选择协议 protocol");
        return;
    }
    if (!f.ip?.trim()) {
        ElMessage.warning("请输入主机地址 host");
        return;
    }
    if (!f.port || f.port <= 0) {
        ElMessage.warning("请输入有效端口 port");
        return;
    }
    if (f.protocol === "OpcUa" && !String(f.transferProtocol ?? "").trim()) {
        ElMessage.warning("西门子 OPC UA 设备请配置文件传输协议（FTP 或 SMB）");
        return;
    }
    if (String(f.transferProtocol ?? "").trim() && !String(f.transferHost ?? "").trim()) {
        ElMessage.warning("请填写文件传输主机");
        return;
    }
    if (String(f.transferProtocol ?? "").trim() && (!f.transferPort || f.transferPort <= 0)) {
        ElMessage.warning("请填写有效文件传输端口");
        return;
    }
    if (f.transferProtocol === "SMB" && !String(f.transferShareName ?? "").trim()) {
        ElMessage.warning("SMB 文件传输请填写共享名 ShareName");
        return;
    }
    if (f.protocol === "NCLink" && !String(f.deviceGuid ?? "").trim()) {
        ElMessage.warning("NCLink 须填写 DeviceGuid（现场 NC-Link 设备唯一标识）");
        return;
    }
    if (f.protocol === "NCLinkApi" && !String(f.ncLinkApiDeviceId ?? "").trim()) {
        ElMessage.warning("NC-Link API Server 须填写 DeviceId（机床 SN 码）");
        return;
    }
    if (f.protocol === "GskWebServer" && !String(f.gskDeviceSn ?? "").trim()) {
        ElMessage.warning("广数 GSK WebServer 须填写 DeviceSn（设备序列号，与 swagger 路径 /api/v1/{DeviceSn} 一致）");
        return;
    }

    const ext = buildExtendedProps(f as unknown as Record<string, unknown>);
    const extendedProperties =
        Object.keys(ext).length > 0 ? ext : undefined;
    const connectTimeoutMs =
        typeof f.connectTimeoutMs === "number" && f.connectTimeoutMs > 0
            ? f.connectTimeoutMs
            : undefined;
    const readTimeoutMs =
        typeof f.readTimeoutMs === "number" && f.readTimeoutMs > 0
            ? f.readTimeoutMs
            : undefined;
    const username = String(f.username ?? "").trim() || undefined;
    const password = String(f.password ?? "").trim() || undefined;
    const transfer = buildProgramTransferConfig(f);
    try {
        if (f.id) {
            const saved = await machineConnectionDevicesApi.update(f.id, {
                name: f.name,
                type: f.deviceType,
                brand: f.brand.trim(),
                model: f.model.trim(),
                protocol: f.protocol,
                host: f.ip.trim(),
                port: f.port,
                username,
                password,
                connectTimeoutMs,
                readTimeoutMs,
                extendedProperties,
                transfer,
            });
            if (saved.upstreamSynced === false)
                ElMessage.warning(`已保存到网关，但同步采集服务失败：${saved.upstreamError ?? "上游不可用"}（可稍后点「同步到采集服务」重试）`);
            else ElMessage.success("已保存");
        } else {
            const saved = await machineConnectionDevicesApi.create({
                name: f.name,
                type: f.deviceType,
                brand: f.brand.trim(),
                model: f.model.trim(),
                protocol: f.protocol,
                host: f.ip.trim(),
                port: f.port,
                username,
                password,
                connectTimeoutMs,
                readTimeoutMs,
                extendedProperties,
                transfer,
            });
            if (saved.upstreamSynced === false)
                ElMessage.warning(`设备已创建，但同步采集服务失败：${saved.upstreamError ?? "上游不可用"}（地址浏览/采集等功能需同步成功后可用）`);
            else ElMessage.success("已创建设备");
        }
        dialogVisible.value = false;
        await loadDevices();
    } catch (err: unknown) {
        const ax = err as { response?: { data?: { error?: string; detail?: string } } };
        const msg =
            ax.response?.data?.error ??
            ax.response?.data?.detail ??
            (err instanceof Error ? err.message : "保存失败");
        ElMessage.error(String(msg));
    }
};

// 查看设备详情
const viewDeviceDetail = (device: any) => {
    // 只读详情弹窗
    detailDevice.value = device as DeviceUi;
    detailDialogVisible.value = true;
};

// 删除设备
const deleteDevice = async (id: string) => {
    try {
        await ElMessageBox.confirm("确定删除该设备？", "确认", {
            type: "warning",
            confirmButtonText: "删除",
            cancelButtonText: "取消",
        });
        await machineConnectionDevicesApi.remove(id);
        ElMessage.success("已删除");
        await loadDevices();
    } catch (e) {
        if (e === "cancel") return;
        const ax = e as { response?: { data?: { error?: string } } };
        ElMessage.error(ax.response?.data?.error ?? "删除失败");
    }
};

// 批量新增
const openBatchAddDialog = () => {
    deviceImportInputRef.value?.click();
};

// 导出模板
const exportDeviceTemplate = async () => {
    try {
        const blob = await machineConnectionDevicesApi.downloadTemplate();
        downloadBlob(blob, "device-import-template.csv");
    } catch (e) {
        ElMessage.error(getApiErrorMessage(e, "设备模板导出失败"));
    }
};

// 导入设备
const importDevices = () => {
    deviceImportInputRef.value?.click();
};

// 全量对账：把网关本地设备注册表同步到上游 Industrial IoT（上游按 deviceId 解析驱动）
const syncingUpstream = ref(false);
const syncUpstreamDevices = async () => {
    syncingUpstream.value = true;
    try {
        const report = await machineConnectionDevicesApi.syncUpstream();
        await loadDevices();
        if (report.failed === 0) {
            ElMessage.success(`同步完成：新建 ${report.created}，更新 ${report.updated}，共 ${report.total} 台`);
        } else {
            const detail = report.errors
                .slice(0, 5)
                .map((e) => `${e.name}：${e.error}`)
                .join("\n");
            ElMessageBox.alert(detail, `同步完成，${report.failed}/${report.total} 台失败`, {
                confirmButtonText: "知道了",
                type: "warning",
            }).catch(() => { /* 关闭 */ });
        }
    } catch (e) {
        ElMessage.error(getApiErrorMessage(e, "同步上游失败"));
    } finally {
        syncingUpstream.value = false;
    }
};

// ---------- NC-Link 诊断（Probe 自报模型 / 数据项 / 采样通道） ----------
const nclinkDialogVisible = ref(false);
const nclinkDeviceId = ref("");
const nclinkLoading = ref(false);
const nclinkError = ref("");
const nclinkTab = ref("dataitems");
const nclinkFilter = ref("");
const nclinkProbe = ref<NCLinkProbeModel | null>(null);

const nclinkDevices = computed(() =>
    devices.value.filter((d) => (d.protocol || "").toLowerCase().includes("nclink")),
);

const filteredNclinkItems = computed(() => {
    const items = nclinkProbe.value?.dataItems ?? [];
    const kw = nclinkFilter.value.trim().toLowerCase();
    if (!kw) return items;
    return items.filter(
        (x) => x.id.toLowerCase().includes(kw) || (x.name || "").toLowerCase().includes(kw),
    );
});

const openNclinkDialog = () => {
    nclinkDialogVisible.value = true;
    if (!nclinkDeviceId.value && nclinkDevices.value.length) {
        nclinkDeviceId.value = nclinkDevices.value[0]?.id ?? "";
    }
    if (!nclinkDevices.value.length) {
        ElMessage.warning("当前没有 NC-Link 协议的设备（协议为 NCLink / NCLinkApi）");
    }
};

const loadNclinkDiagnostics = async () => {
    if (!nclinkDeviceId.value) return;
    nclinkLoading.value = true;
    nclinkError.value = "";
    nclinkProbe.value = null;
    try {
        nclinkProbe.value = await machineConnectionDiagnosticsApi.nclinkProbe(nclinkDeviceId.value);
    } catch (e: unknown) {
        nclinkError.value = getApiErrorMessage(e, "读取 Probe 模型失败：请确认设备为 NC-Link 协议且可连接");
    } finally {
        nclinkLoading.value = false;
    }
};

const handleDeviceImportFile = async (event: Event) => {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = "";
    if (!file) return;
    try {
        const result = await machineConnectionDevicesApi.importDevices(file);
        await loadDevices();
        const message = `导入完成：成功 ${result.success}/${result.total}，失败 ${result.failed}`;
        if (result.failed > 0) ElMessage.warning(`${message}。${result.errors.slice(0, 2).join("；")}`);
        else ElMessage.success(message);
    } catch (e) {
        ElMessage.error(getApiErrorMessage(e, "设备批量导入失败"));
    }
};

function stopCollectionTimers(deviceId?: string) {
    if (deviceId) {
        const timerIds = collectionTimerIdsByDevice.value[deviceId] ?? [];
        for (const id of timerIds) {
            window.clearInterval(id);
        }
        delete collectionTimerIdsByDevice.value[deviceId];
        collectingDeviceIds.value = collectingDeviceIds.value.filter((id) => id !== deviceId);
        return;
    }

    for (const timerIds of Object.values(collectionTimerIdsByDevice.value)) {
        for (const id of timerIds) {
            window.clearInterval(id);
        }
    }
    collectionTimerIdsByDevice.value = {};
    collectingDeviceIds.value = [];
}

const isCollectingDevice = (deviceId: string) => {
    return collectingDeviceIds.value.includes(deviceId);
};

const stopCollection = (deviceId?: string) => {
    if (collectingDeviceIds.value.length === 0) {
        ElMessage.info("当前没有进行中的采集任务");
        return;
    }
    if (deviceId && !collectingDeviceIds.value.includes(deviceId)) {
        ElMessage.info("该设备当前未在采集中");
        return;
    }
    stopCollectionTimers(deviceId);
    collectionLoading.value = false;
    ElMessage.success(deviceId ? "该设备已停止采集" : "已停止全部采集");
};

function formatRangeTime(mode: CollectionHistoryMode): { start: Date; end: Date } | null {
    const now = new Date();
    if (mode === "day") {
        const start = new Date(now);
        start.setHours(0, 0, 0, 0);
        return { start, end: now };
    }
    if (mode === "week") {
        const start = new Date(now);
        const day = start.getDay();
        const diff = day === 0 ? 6 : day - 1;
        start.setDate(start.getDate() - diff);
        start.setHours(0, 0, 0, 0);
        return { start, end: now };
    }
    if (mode === "month") {
        const start = new Date(now.getFullYear(), now.getMonth(), 1, 0, 0, 0, 0);
        return { start, end: now };
    }
    if (collectionHistoryCustomDateRange.value.length === 2) {
        const [startRaw, endRaw] = collectionHistoryCustomDateRange.value;
        const start = new Date(`${startRaw}T00:00:00`);
        const end = new Date(`${endRaw}T23:59:59.999`);
        if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime()) || end <= start) {
            return null;
        }
        return { start, end };
    }
    return null;
}

function mapHistoryItemToRow(item: InfluxTelemetryHistoryItem): CollectionHistoryRow {
    return {
        displayName: item.name || "-",
        path: item.path,
        dataType: item.dataType,
        value: item.value == null ? "-" : stringifyTagValue(item.value),
        quality: item.quality || "-",
        status: item.status || "-",
        errorMessage: item.errorMessage || "-",
        time: item.time ? new Date(item.time).toLocaleString() : "-",
    };
}

const queryCollectionHistory = async (resetPage = false) => {
    const deviceId = collectionHistoryDeviceId.value;
    if (!deviceId) {
        ElMessage.warning("请选择设备");
        return;
    }
    if (resetPage) {
        collectionHistoryCurrentPage.value = 1;
    }
    const range = formatRangeTime(collectionHistoryMode.value);
    if (!range) {
        ElMessage.warning("请选择合法的日期起止范围");
        return;
    }

    collectionHistoryLoading.value = true;
    try {
        const rows = await telemetryInfluxApi.history({
            deviceId,
            startTime: range.start.toISOString(),
            endTime: range.end.toISOString(),
            page: collectionHistoryCurrentPage.value,
            pageSize: collectionHistoryPageSize.value,
        });
        collectionHistoryRows.value = rows.items.map(mapHistoryItemToRow);
        collectionHistoryTotal.value = rows.total || 0;
    } catch (e: unknown) {
        console.error(e);
        const message = getApiErrorMessage(e, "查询历史采集记录失败");
        if (message.toLowerCase().includes("not found")) {
            collectionHistoryRows.value = [];
            collectionHistoryTotal.value = 0;
            ElMessage.info("当前时序库暂不支持该查询或暂无历史数据");
            return;
        }
        ElMessage.error(message);
    } finally {
        collectionHistoryLoading.value = false;
    }
};

const openCollectionHistoryDialog = async (deviceId?: string) => {
    const id = deviceId || collectionDeviceId.value;
    if (!id) {
        ElMessage.warning("请选择设备");
        return;
    }
    collectionHistoryDeviceId.value = id;
    collectionHistoryDeviceName.value = devices.value.find((d) => d.id === id)?.name ?? id;
    collectionHistoryMode.value = "day";
    collectionHistoryCustomDateRange.value = [];
    collectionHistoryRows.value = [];
    collectionHistoryCurrentPage.value = 1;
    collectionHistoryPageSize.value = 20;
    collectionHistoryTotal.value = 0;
    collectionHistoryDialogVisible.value = true;
    await queryCollectionHistory(true);
};

const handleCollectionHistorySizeChange = async (size: number) => {
    collectionHistoryPageSize.value = size;
    collectionHistoryCurrentPage.value = 1;
    await queryCollectionHistory();
};

const handleCollectionHistoryCurrentChange = async (current: number) => {
    collectionHistoryCurrentPage.value = current;
    await queryCollectionHistory();
};

function upsertCollectionRows(partial: CollectionRow[]) {
    const next = new Map(collectionRows.value.map((r) => [r.path, r]));
    for (const row of partial) {
        next.set(row.path, row);
    }
    collectionRows.value = Array.from(next.values());
}

function upsertCollectionRowsByDevice(deviceId: string, partial: CollectionRow[]) {
    const current = collectionRowsByDevice.value[deviceId] ?? [];
    const next = new Map(current.map((r) => [r.path, r]));
    for (const row of partial) {
        next.set(row.path, row);
    }
    collectionRowsByDevice.value[deviceId] = Array.from(next.values());
}

async function runCollectionBatch(deviceId: string, points: SavedCollectionPoint[]) {
    if (points.length === 0) return;
    const resp = await machineConnectionPointsApi.readTags(deviceId, {
        tags: points.map((p) => ({
            address: p.address,
            dataType: p.dataType,
        })),
    });
    const map = new Map(resp.tags.map((t) => [normalizeTagAddress(t.address), t]));
    const now = new Date().toLocaleString();
    const rows: CollectionRow[] = points.map((p) => {
        const r = map.get(normalizeTagAddress(p.address));
        const hasError = !r || !!r.errorMessage?.trim();
        return {
            displayName: p.displayName,
            path: p.path,
            dataType: p.dataType,
            value: hasError ? "-" : stringifyTagValue(r?.value),
            status: hasError ? "失败" : "成功",
            time: now,
        };
    });
    upsertCollectionRowsByDevice(deviceId, rows);
    if (collectionDeviceId.value === deviceId) {
        upsertCollectionRows(rows);
    }

    void telemetryInfluxApi
        .writeBatch({
            deviceId,
            collectedAt: new Date().toISOString(),
            points: points.map((p) => {
                const r = map.get(normalizeTagAddress(p.address));
                const hasError = !!r?.errorMessage?.trim();
                return {
                    name: p.displayName,
                    path: p.path,
                    dataType: p.dataType,
                    value: hasError ? null : (r?.value ?? null),
                    quality: r?.quality ?? "",
                    timestamp: r?.timestamp ?? "",
                    status: hasError ? "失败" : "成功",
                    errorMessage: hasError ? r?.errorMessage ?? null : null,
                };
            }),
        })
        .catch((e: unknown) => {
            console.error("InfluxDB 写入失败", e);
        });
}

const runCollection = async (deviceIdFromCard?: string) => {
    const deviceId = deviceIdFromCard || collectionDeviceId.value;
    if (!deviceId) {
        ElMessage.warning("请选择设备");
        return;
    }
    collectionDeviceId.value = deviceId;
    collectionDialogDeviceName.value =
        devices.value.find((d) => d.id === deviceId)?.name ?? deviceId;
    collectionDialogVisible.value = true;
    collectionRows.value = collectionRowsByDevice.value[deviceId] ?? [];
    stopCollectionTimers(deviceId);
    let points: SavedCollectionPoint[] = [];
    try {
        // 每次开始采集都以数据库 datacollection 为准，避免使用旧缓存频率
        const rows = await datacollectionApi.list(deviceId);
        points = rows.map((r) => ({
            address: r.path,
            dataType: mapDbDatatypeToReadType(r.datatype),
            displayName: r.name,
            path: r.path,
            collectionFrequency: Number(r.collectionFrequency || 500),
        }));
        savedCollectionPointsByDevice.value[deviceId] = points;
    } catch (e: unknown) {
        console.error(e);
        ElMessage.warning("该设备暂无已保存采集点位，请先在“点位配置”中勾选并保存（或检查数据库连接）");
        return;
    }
    if (points.length === 0) {
        ElMessage.warning("该设备暂无已保存采集点位，请先在“点位配置”中勾选并保存");
        return;
    }

    collectionLoading.value = true;
    try {
        collectionRowsByDevice.value[deviceId] = [];
        collectionRows.value = [];
        const groups = new Map<number, SavedCollectionPoint[]>();
        for (const p of points) {
            const freq = Number(p.collectionFrequency || 500);
            const key = Number.isFinite(freq) && freq > 0 ? freq : 500;
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key)!.push(p);
        }

        // 先执行一轮立即采集，再按各自频率定时采集
        for (const [freq, groupPoints] of groups.entries()) {
            await runCollectionBatch(deviceId, groupPoints);
            const timerId = window.setInterval(() => {
                void runCollectionBatch(deviceId, groupPoints).catch((e: unknown) => {
                    console.error(e);
                });
            }, freq);
            if (!collectionTimerIdsByDevice.value[deviceId]) {
                collectionTimerIdsByDevice.value[deviceId] = [];
            }
            collectionTimerIdsByDevice.value[deviceId].push(timerId);
        }
        if (!collectingDeviceIds.value.includes(deviceId)) {
            collectingDeviceIds.value.push(deviceId);
        }
        ElMessage.success(`已启动采集（${points.length}项，${groups.size}个频率组）`);
    } catch (e: unknown) {
        const ax = e as { response?: { data?: { error?: string } } };
        ElMessage.error(ax.response?.data?.error ?? "数据采集失败");
    } finally {
        collectionLoading.value = false;
    }
};

// 连接测试（需已持久化的设备 ID）
const testConnection = async (deviceId?: string) => {
    const id = deviceId ?? deviceForm.value.id;
    if (!id) {
        ElMessage.warning("请先保存设备，再执行连接测试");
        return;
    }
    try {
        const r = await machineConnectionDevicesApi.testConnection(id);
        if (r.success) {
            ElMessage.success(
                r.latency ? `连接成功，延迟 ${r.latency}` : "连接成功",
            );
        } else {
            ElMessage.error(r.errorMessage || "连接失败");
        }
    } catch (e) {
        const ax = e as { response?: { data?: { error?: string } } };
        ElMessage.error(ax.response?.data?.error ?? "连接测试失败");
    }
};

watch(collectionDeviceId, () => {
    collectionRows.value = collectionRowsByDevice.value[collectionDeviceId.value] ?? [];
});

watch([refreshMode, pointDialogVisible], ([mode, visible]) => {
    if (!visible || mode === "off") {
        stopPointAutoRefresh();
        return;
    }
    const interval = mode === "5s" ? 5000 : mode === "10s" ? 10000 : 0;
    if (interval > 0) {
        startPointAutoRefresh(interval);
    } else {
        stopPointAutoRefresh();
    }
});

onBeforeUnmount(() => {
    stopPointAutoRefresh();
    stopCollectionTimers();
});

// 分页处理
const handleSizeChange = (size: number) => {
    pageSize.value = size;
    currentPage.value = 1;
};

const handleCurrentChange = (current: number) => {
    currentPage.value = current;
};
</script>

<style lang="scss" scoped>
.device-view {
    .device-layout {
        display: grid;
        grid-template-columns: 260px 1fr;
        gap: 16px;
    }

    .tree-panel {
        .tree-title {
            font-weight: 600;
        }

        .tree-search {
            margin-bottom: 10px;
        }
    }

    .point-layout {
        display: grid;
        grid-template-columns: 280px 1fr;
        gap: 12px;
    }

    .point-tree-panel {
        height: 100%;
    }

    :deep(.el-tree-node.is-current > .el-tree-node__content) {
        background: var(--el-color-primary-light-9);
    }

    .point-tree-node {
        display: flex;
        align-items: center;
        padding: 0 4px;
    }

    .point-tree-node.is-selected {
        font-weight: 600;
        color: var(--el-color-primary);
    }

    .point-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        font-weight: 600;
        margin-bottom: 10px;
    }

    .point-header-actions,
    .point-toolbar {
        display: flex;
        gap: 6px;
    }

    .collection-toolbar {
        display: flex;
        align-items: center;
        gap: 8px;
        margin-bottom: 10px;
    }

    .collection-history-toolbar {
        display: flex;
        align-items: center;
        flex-wrap: wrap;
        gap: 10px;
        margin-bottom: 10px;
    }

    .collection-history-pagination {
        margin-top: 10px;
        display: flex;
        justify-content: flex-end;
    }

    .collection-hint {
        font-size: 12px;
        color: var(--el-text-color-secondary);
    }

    .point-table-footer {
        margin-top: 10px;
        display: flex;
        justify-content: flex-end;
    }


    .freq-input {
        display: flex;
        align-items: center;
        gap: 6px;
    }

    .freq-unit {
        color: var(--el-text-color-secondary);
        font-size: 12px;
    }


    .transfer-toolbar {
        display: flex;
        align-items: center;
        gap: 8px;
        margin-bottom: 10px;

        &--form {
            flex-direction: column;
            align-items: stretch;
            gap: 10px;
        }
    }

    .transfer-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;

        &--actions {
            justify-content: flex-end;
        }
    }

    .transfer-label {
        flex: 0 0 auto;
        min-width: 72px;
        font-size: 12px;
        color: var(--el-text-color-regular);
    }

    .transfer-channel-detail {
        flex: 1 1 360px;
        min-width: 240px;
        font-size: 12px;
        color: var(--el-text-color-secondary);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
    }

    .transfer-file-name {
        flex: 1 1 200px;
        min-width: 200px;
        font-size: 12px;
        color: var(--el-text-color-secondary);
        max-width: 620px;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
    }

    .transfer-path-result {
        flex: 1 1 200px;
        min-width: 0;
        max-width: 620px;
        display: flex;
        align-items: center;
        gap: 8px;

        &__path {
            flex: 1 1 auto;
            min-width: 0;
            font-size: 12px;
            color: var(--el-text-color-secondary);
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
            cursor: default;
        }

        &__tag {
            flex-shrink: 0;
        }
    }

    .transfer-batch-result {
        flex: 1 1 200px;
        min-width: 0;
        max-width: 620px;

        &__text {
            display: inline-block;
            width: 100%;
            font-size: 12px;
            color: var(--el-text-color-secondary);
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
            cursor: default;
        }
    }

    .remote-path-picker {
        border: 1px solid var(--el-border-color-lighter);
        border-radius: 6px;
        padding: 10px;
        max-height: 420px;
        overflow: auto;
    }

    .remote-path-node {
        display: flex;
        align-items: flex-start;
        gap: 6px;

        &__text {
            display: flex;
            flex-direction: column;
            min-width: 0;
            line-height: 1.2;
        }

        &__label {
            color: var(--el-text-color-primary);
        }

        &__path {
            margin-top: 2px;
            font-size: 12px;
            color: var(--el-text-color-secondary);
            word-break: break-all;
        }
    }

    .remote-path-preview {
        margin-top: 10px;
        padding: 8px 10px;
        background: var(--el-fill-color-light);
        border-radius: 4px;
        font-size: 12px;
    }

    .remote-path-preview__label {
        color: var(--el-text-color-secondary);
    }

    .remote-path-preview__value {
        color: var(--el-text-color-primary);
        word-break: break-all;
    }

    .devices-api-hint {
        margin-bottom: 16px;
        padding: 12px 14px;
        font-size: 13px;
        line-height: 1.5;
        color: var(--el-text-color-regular);
        background: var(--el-fill-color-light);
        border-radius: 8px;
        border: 1px solid var(--el-border-color-lighter);

        code {
            font-size: 12px;
            padding: 0 4px;
            background: var(--el-fill-color);
            border-radius: 4px;
        }

        &--error {
            color: var(--el-color-danger);
            border-color: var(--el-color-danger-light-5);
            background: var(--el-color-danger-light-9);
        }
    }

    .action-bar {
        display: flex;
        align-items: center;
        margin-bottom: 20px;
        gap: 10px;
    }

    .device-cards {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(400px, 1fr));
        gap: 20px;
        margin-bottom: 20px;

        .device-card {
            border-radius: 8px;
            box-shadow: 0 2px 12px 0 rgba(0, 0, 0, 0.1);
            transition: all 0.3s ease;

            &:hover {
                box-shadow: 0 4px 16px 0 rgba(0, 0, 0, 0.15);
                transform: translateY(-2px);
            }

            .card-header {
                display: flex;
                justify-content: space-between;
                align-items: flex-start;
                margin-bottom: 15px;
                padding-bottom: 10px;
                border-bottom: 1px solid var(--el-border-color);

                .device-info {
                    .device-name {
                        margin: 0 0 5px 0;
                        font-size: 18px;
                        font-weight: 600;
                    }

                    .device-code {
                        margin: 0;
                        font-size: 14px;
                        color: var(--el-text-color-secondary);
                    }
                }

                .device-status {
                    .el-tag {
                        font-size: 12px;
                        padding: 4px 8px;
                    }
                }
            }

            .card-body {
                margin-bottom: 15px;

                .info-row {
                    .el-descriptions {
                        width: 100%;

                        .el-descriptions__cell {
                            padding: 8px 0;
                        }

                        .el-descriptions__label {
                            font-weight: 500;
                            color: var(--el-text-color-secondary);
                        }
                    }
                }
            }

            .card-footer {
                display: flex;
                flex-direction: column;
                align-items: flex-start;
                gap: 4px;
                padding-top: 15px;
                border-top: 1px solid var(--el-border-color);

                .card-footer-row {
                    display: flex;
                    flex-wrap: wrap;
                    gap: 10px;
                }
            }
        }
    }

    .pagination {
        margin-top: 20px;
        display: flex;
        justify-content: flex-end;
    }

    .dialog-footer {
        display: flex;
        justify-content: flex-end;
        gap: 10px;
    }
}

@media (max-width: 1200px) {
    .device-view .device-layout {
        grid-template-columns: 1fr;
    }

    .device-view .point-layout {
        grid-template-columns: 1fr;
    }
}
</style>
