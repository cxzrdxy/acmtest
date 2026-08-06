import axios from 'axios'

const http = axios.create({ baseURL: '/api/v1' })

// 请求拦截器：自动附加 JWT（token 直接读 localStorage，不依赖 store，避免循环依赖）
http.interceptors.request.use((config) => {
  const token = localStorage.getItem('acm_token')
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

// 响应拦截器：统一错误处理
http.interceptors.response.use(
  (resp) => resp.data, // 成功：直接返回 data，调用方不用剥壳
  (err) => {
    const { response } = err
    if (response) {
      if (response.status === 401) {
        // 区分两类 401：带 token 的请求 401 = token 失效；没带 token（登录/注册）的 401 = 账号密码错误
        const hadToken = !!err.config?.headers?.Authorization
        if (hadToken) {
          localStorage.removeItem('acm_token')
          localStorage.removeItem('acm_user')
          window.location.href = '/login'
          return Promise.reject(err)
        }
      }
      // 业务错误 {detail} 或 400 校验错误 {errors} 的第一条消息
      const detail =
        response.data?.detail ??
        Object.values(response.data?.errors ?? {}).flat()[0] ??
        '请求失败'
      alert(detail)
    } else {
      alert('网络错误或服务器异常')
    }
    return Promise.reject(err)
  }
)

// 鉴权接口（对齐后端 /api/v1/auth）
export const authApi = {
  register: (data) => http.post('/auth/register', data),
  login: (data) => http.post('/auth/login', data),
  me: () => http.get('/auth/me'),
  changePassword: (data) => http.put('/auth/change-password', data)
}

// 题目接口（对齐后端 /api/v1/problems）
export const problemApi = {
  list: (params) => http.get('/problems', { params }),
  get: (pid) => http.get(`/problems/${pid}`),
  getBySlug: (slug) => http.get(`/problems/by-slug/${slug}`),
  create: (data) => http.post('/problems', data),
  update: (pid, data) => http.put(`/problems/${pid}`, data),
  remove: (pid) => http.delete(`/problems/${pid}`)
}

// 提交接口（对齐后端 /api/v1）
export const submissionApi = {
  submit: (pid, data) => http.post(`/problems/${pid}/submissions`, data),
  get: (sid) => http.get(`/submissions/${sid}`)
}

export default http
