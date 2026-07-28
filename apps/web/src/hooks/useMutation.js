import { useCallback, useRef, useState } from 'react';
import { useQueryClient } from '../data/QueryClientProvider';

export default function useMutation({ mutationFn, onMutate, onSuccess, onError, onSettled } = {}) {
  const client = useQueryClient();
  const latestCallRef = useRef(0);
  const [state, setState] = useState({ status: 'idle', data: undefined, error: null });

  const mutateAsync = useCallback(async (variables) => {
    const callId = latestCallRef.current + 1;
    latestCallRef.current = callId;
    setState((previous) => ({ ...previous, status: 'pending', error: null }));
    let context;
    try {
      context = await onMutate?.(variables, client);
      const data = await mutationFn(variables, client);
      await onSuccess?.(data, variables, context, client);
      if (latestCallRef.current === callId) setState({ status: 'success', data, error: null });
      return data;
    } catch (error) {
      await onError?.(error, variables, context, client);
      if (latestCallRef.current === callId) setState((previous) => ({ ...previous, status: 'error', error }));
      throw error;
    } finally {
      await onSettled?.(variables, context, client);
    }
  }, [client, mutationFn, onError, onMutate, onSettled, onSuccess]);

  const mutate = useCallback((variables) => {
    mutateAsync(variables).catch(() => {});
  }, [mutateAsync]);

  const reset = useCallback(() => setState({ status: 'idle', data: undefined, error: null }), []);

  return {
    ...state,
    isPending: state.status === 'pending',
    isSuccess: state.status === 'success',
    isError: state.status === 'error',
    mutate,
    mutateAsync,
    reset,
  };
}
