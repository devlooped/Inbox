package rpc

import (
	"bytes"
	"encoding/json"
	"fmt"
)

const Version = "0.1"

// Application error codes in the JSON-RPC reserved range -32000…-32099.
const (
	CodeNotInitialized     = -32001
	CodeAlreadyInitialized = -32002
	CodeStoreRequired      = -32003
	CodeStoreMismatch      = -32004
	CodeStoreLocked        = -32005
	CodeUnsupportedVersion = -32006
	CodeNotPaired          = -32007
	CodePairError          = -32008
	CodeNotFound           = -32009
	CodeInvalidTopic       = -32010
	CodeFilesRequired      = -32011
	CodePathEscape         = -32012
	CodeInvalidParams      = -32013
	CodeDisconnected       = -32014
	CodeUnsupported        = -32015
	CodeParseError         = -32700
	CodeInvalidRequest     = -32600
	CodeMethodNotFound     = -32601
)

const (
	TokNotInitialized     = "not_initialized"
	TokAlreadyInitialized = "already_initialized"
	TokStoreRequired      = "store_required"
	TokStoreMismatch      = "store_mismatch"
	TokStoreLocked        = "store_locked"
	TokUnsupportedVersion = "unsupported_version"
	TokNotPaired          = "not_paired"
	TokPairError          = "pair_error"
	TokNotFound           = "not_found"
	TokInvalidTopic       = "invalid_topic"
	TokFilesRequired      = "files_required"
	TokPathEscape         = "path_escape"
	TokInvalidParams      = "invalid_params"
	TokDisconnected       = "disconnected"
	TokUnsupported        = "unsupported"
	TokParseError         = "parse_error"
	TokInvalidRequest     = "invalid_request"
	TokMethodNotFound     = "method_not_found"
)

type Request struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id"`
	Method  string          `json:"method"`
	Params  json.RawMessage `json:"params"`
}

type Error struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    any    `json:"data,omitempty"`
}

func (e *Error) Error() string {
	if e == nil {
		return ""
	}
	if e.Data != nil {
		return fmt.Sprintf("%s (%d): %v", e.Message, e.Code, e.Data)
	}
	return e.Message
}

func NewError(code int, token string, data any) *Error {
	return &Error{Code: code, Message: token, Data: data}
}

func Err(token string) *Error {
	code, ok := tokenCode[token]
	if !ok {
		code = CodeInvalidParams
	}
	return NewError(code, token, nil)
}

func ErrData(token string, data any) *Error {
	e := Err(token)
	e.Data = data
	return e
}

var tokenCode = map[string]int{
	TokNotInitialized:     CodeNotInitialized,
	TokAlreadyInitialized: CodeAlreadyInitialized,
	TokStoreRequired:      CodeStoreRequired,
	TokStoreMismatch:      CodeStoreMismatch,
	TokStoreLocked:        CodeStoreLocked,
	TokUnsupportedVersion: CodeUnsupportedVersion,
	TokNotPaired:          CodeNotPaired,
	TokPairError:          CodePairError,
	TokNotFound:           CodeNotFound,
	TokInvalidTopic:       CodeInvalidTopic,
	TokFilesRequired:      CodeFilesRequired,
	TokPathEscape:         CodePathEscape,
	TokInvalidParams:      CodeInvalidParams,
	TokDisconnected:       CodeDisconnected,
	TokUnsupported:        CodeUnsupported,
	TokParseError:         CodeParseError,
	TokInvalidRequest:     CodeInvalidRequest,
	TokMethodNotFound:     CodeMethodNotFound,
}

func Result(id json.RawMessage, result any) []byte {
	return mustMarshal(map[string]any{
		"jsonrpc": "2.0",
		"id":      rawOrNull(id),
		"result":  result,
	})
}

func ErrorLine(id json.RawMessage, err *Error) []byte {
	return mustMarshal(map[string]any{
		"jsonrpc": "2.0",
		"id":      rawOrNull(id),
		"error":   err,
	})
}

func Event(params any) []byte {
	return mustMarshal(map[string]any{
		"jsonrpc": "2.0",
		"method":  "event",
		"params":  params,
	})
}

func Parse(line []byte) (*Request, *Error) {
	line = bytes.TrimSpace(line)
	if len(line) == 0 {
		return nil, NewError(CodeInvalidRequest, TokInvalidRequest, "empty")
	}
	var req Request
	if err := json.Unmarshal(line, &req); err != nil {
		return nil, NewError(CodeParseError, TokParseError, err.Error())
	}
	if req.JSONRPC != "2.0" || req.Method == "" {
		return nil, NewError(CodeInvalidRequest, TokInvalidRequest, nil)
	}
	return &req, nil
}

func DecodeParams(raw json.RawMessage, dest any) *Error {
	if len(raw) == 0 || string(raw) == "null" {
		return nil
	}
	if err := json.Unmarshal(raw, dest); err != nil {
		return ErrData(TokInvalidParams, err.Error())
	}
	return nil
}

func mustMarshal(v any) []byte {
	b, err := json.Marshal(v)
	if err != nil {
		b, _ = json.Marshal(map[string]any{
			"jsonrpc": "2.0",
			"id":      nil,
			"error":   NewError(CodeParseError, TokParseError, err.Error()),
		})
	}
	return b
}

func rawOrNull(id json.RawMessage) any {
	if len(id) == 0 {
		return nil
	}
	return json.RawMessage(id)
}
