import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css' // 暗黑模式样式
import zhCn from 'element-plus/es/locale/lang/zh-cn' // 中文语言包
import App from './App.vue'
import router from './router'
import './styles/index.scss'

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(ElementPlus, {
    locale: zhCn, // 设置中文
    size: 'default', // 全局组件尺寸
    zIndex: 3000 // 全局 z-index
})

app.mount('#app')
