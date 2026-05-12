import * as React from 'react'

/**
 * 工具调用信息
 */
export interface ToolCall {
  id: string
  name: string
  arguments: Record<string, unknown>
}

/**
 * 工具执行结果
 */
export interface ToolResult {
  toolCallId: string
  result: string
  isError: boolean
}

/**
 * 引用的选中文本
 */
export interface QuotedText {
  title?: string
  text: string
}

/**
 * Token 使用统计
 */
export interface TokenUsage {
  inputTokens: number
  outputTokens: number
}

/**
 * 内容块类型
 */
export type ContentBlockType = 'thinking' | 'text' | 'tool_call' | 'tool_result'

/**
 * 内容块
 */
export interface ContentBlock {
  type: ContentBlockType
  content?: string           // thinking 或 text 内容
  toolCall?: ToolCall        // tool_call 时的工具调用信息
  toolResult?: ToolResult    // tool_result 时的工具执行结果
}

/**
 * 聊天消息
 */
export interface ChatMessage {
  id: string
  role: 'user' | 'assistant' | 'tool'
  content: string
  thinking?: string          // AI 思考内容
  contentBlocks?: ContentBlock[]  // 按顺序存储的内容块
  images?: string[]          // Base64编码的图片
  quotedText?: QuotedText    // 引用的选中文本
  toolCalls?: ToolCall[]     // 工具调用 (兼容旧结构)
  toolResult?: ToolResult    // 工具结果
  isHidden?: boolean         // 仅用于上下文传递，不在消息列表直接展示
  tokenUsage?: TokenUsage    // Token 使用统计
  timestamp: number
}

/**
 * 新消息输入（不包含自动生成的字段）
 */
export type NewChatMessage = Omit<ChatMessage, 'id' | 'timestamp'>

/**
 * 消息更新
 */
export type ChatMessageUpdate = Partial<Omit<ChatMessage, 'id' | 'timestamp'>>

/**
 * useChatHistory Hook 返回类型
 */
export interface UseChatHistoryReturn {
  messages: ChatMessage[]
  addMessage: (msg: NewChatMessage) => string
  updateMessage: (id: string, updates: ChatMessageUpdate) => void
  clearHistory: () => void
}

/**
 * 生成唯一消息ID
 */
function generateMessageId(): string {
  return `msg_${Date.now()}_${Math.random().toString(36).substring(2, 9)}`
}

/**
 * 对话历史管理Hook
 * 
 * 功能：
 * - 维护完整的对话历史（用户消息、AI回复、工具调用和工具结果）
 * - 支持添加、更新、清空消息
 * - 页面刷新时自动清空（不持久化）
 * 
 * @returns UseChatHistoryReturn
 * 
 * Requirements: 8.1, 8.2, 8.4
 */
export function useChatHistory(): UseChatHistoryReturn {
  const [messages, setMessages] = React.useState<ChatMessage[]>([])

  /**
   * 添加新消息到历史记录
   * @param msg 新消息（不包含id和timestamp）
   * @returns 生成的消息ID
   */
  const addMessage = React.useCallback((msg: NewChatMessage): string => {
    const id = generateMessageId()
    const newMessage: ChatMessage = {
      ...msg,
      id,
      timestamp: Date.now(),
    }
    setMessages(prev => [...prev, newMessage])
    return id
  }, [])

  /**
   * 更新指定ID的消息
   * @param id 消息ID
   * @param updates 要更新的字段
   */
  const updateMessage = React.useCallback((id: string, updates: ChatMessageUpdate): void => {
    setMessages(prev =>
      prev.map(msg =>
        msg.id === id ? { ...msg, ...updates } : msg
      )
    )
  }, [])

  /**
   * 清空所有对话历史
   */
  const clearHistory = React.useCallback((): void => {
    setMessages([])
  }, [])

  return {
    messages,
    addMessage,
    updateMessage,
    clearHistory,
  }
}
