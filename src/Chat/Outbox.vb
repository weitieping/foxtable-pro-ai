Namespace AI

    ''' 发往网页的消息队列。后台线程只管往里放, 界面线程约每 50 毫秒取一批推给网页。
    ''' 相邻的同一条消息的流式片段合并成一个, 避免网页刷新过频。
    Friend NotInheritable Class Outbox

        Private ReadOnly _lock As New Object
        Private _items As New List(Of JObject)

        Public Sub Enqueue(evt As JObject)
            SyncLock _lock
                _items.Add(evt)
            End SyncLock
        End Sub

        ''' 流式片段: 与队尾同一消息、同一类的片段合并。
        Public Sub Delta(id As String, kind As String, text As String)
            SyncLock _lock
                If _items.Count > 0 Then
                    Dim last = _items(_items.Count - 1)
                    If CStr(last("type")) = "delta" AndAlso CStr(last("id")) = id AndAlso CStr(last("kind")) = kind Then
                        last("text") = CStr(last("text")) & text
                        Return
                    End If
                End If
                _items.Add(New JObject From {{"type", "delta"}, {"id", id}, {"kind", kind}, {"text", text}})
            End SyncLock
        End Sub

        Public Sub Clear()
            SyncLock _lock
                _items.Clear()
            End SyncLock
        End Sub

        ''' 取走当前全部消息。
        Public Function Drain() As List(Of JObject)
            SyncLock _lock
                If _items.Count = 0 Then Return Nothing
                Dim res = _items
                _items = New List(Of JObject)
                Return res
            End SyncLock
        End Function
    End Class
End Namespace
