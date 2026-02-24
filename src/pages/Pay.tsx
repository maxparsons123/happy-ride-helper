import { useEffect, useRef, useState } from "react";
import { useParams, useSearchParams } from "react-router-dom";

declare global {
  interface Window {
    SumUpCard: {
      mount: (config: {
        id: string;
        checkoutId: string;
        locale?: string;
        currency?: string;
        amount?: string;
        merchantCode?: string;
        merchantName?: string;
        googlePay?: boolean;
        applePay?: boolean;
        showEmail?: boolean;
        showFooter?: boolean;
        onResponse?: (type: string, body: unknown) => void;
        onLoad?: () => void;
      }) => { submit: () => void; unmount: () => void };
    };
  }
}

const Pay = () => {
  const { checkoutId } = useParams<{ checkoutId: string }>();
  const [searchParams] = useSearchParams();
  const amount = searchParams.get("amount");
  const currency = searchParams.get("currency") || "GBP";
  const description = searchParams.get("desc") || "Taxi fare";

  const [status, setStatus] = useState<"loading" | "ready" | "processing" | "success" | "error">("loading");
  const [message, setMessage] = useState("");
  const mountedRef = useRef(false);

  useEffect(() => {
    if (!checkoutId || mountedRef.current) return;

    const script = document.createElement("script");
    script.src = "https://gateway.sumup.com/gateway/ecom/card/v2/sdk.js";
    script.async = true;
    script.onload = () => {
      if (!window.SumUpCard) {
        setStatus("error");
        setMessage("Failed to load payment widget");
        return;
      }

      mountedRef.current = true;
      window.SumUpCard.mount({
        id: "sumup-card",
        checkoutId,
        locale: "en-GB",
        currency,
        amount: amount || undefined,
        merchantCode: "MW93CBFR",
        merchantName: "Happy Ride",
        showEmail: true,
        showFooter: true,
        googlePay: true,
        applePay: true,
        onLoad: () => setStatus("ready"),
        onResponse: (type: string, body: unknown) => {
          console.log("[SumUp Widget]", type, body);
          switch (type) {
            case "sent":
              setStatus("processing");
              setMessage("Processing your payment...");
              break;
            case "success":
              setStatus("success");
              setMessage("Payment successful! Thank you.");
              break;
            case "error":
            case "fail":
              setStatus("error");
              setMessage("Payment failed. Please try again or contact us.");
              break;
            case "auth-screen":
              setMessage("Please complete authentication...");
              break;
          }
        },
      });
    };

    script.onerror = () => {
      setStatus("error");
      setMessage("Could not load payment system. Please try again.");
    };

    document.body.appendChild(script);

    return () => {
      document.body.removeChild(script);
    };
  }, [checkoutId, amount, currency]);

  if (!checkoutId) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="text-center p-8">
          <h1 className="text-2xl font-bold text-red-600 mb-2">Invalid Payment Link</h1>
          <p className="text-gray-600">This payment link is not valid. Please contact the taxi company.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center p-4">
      <div className="w-full max-w-md">
        <div className="bg-white rounded-2xl shadow-lg overflow-hidden">
          {/* Header */}
          <div className="bg-gradient-to-r from-blue-600 to-blue-700 p-6 text-white text-center">
            <h1 className="text-xl font-bold mb-1">🚕 Taxi Payment</h1>
            <p className="text-blue-100 text-sm">{description}</p>
            {amount && (
              <p className="text-3xl font-bold mt-3">
                {currency === "GBP" ? "£" : currency === "EUR" ? "€" : "$"}
                {amount}
              </p>
            )}
            <p className="text-blue-200 text-xs mt-3">Apple Pay • Google Pay • Card</p>
          </div>

          {/* Wallet buttons */}
          <div className="px-6 pt-5 pb-2 flex gap-3">
            <a
              href={`https://checkout.sumup.com/pay/${checkoutId}`}
              target="_blank"
              rel="noopener noreferrer"
              className="flex-1 flex items-center justify-center gap-2 bg-black text-white rounded-lg py-3 px-4 font-semibold text-sm hover:bg-gray-900 transition-colors"
            >
               Pay
            </a>
            <a
              href={`https://checkout.sumup.com/pay/${checkoutId}`}
              target="_blank"
              rel="noopener noreferrer"
              className="flex-1 flex items-center justify-center gap-2 bg-white border border-gray-300 text-gray-900 rounded-lg py-3 px-4 font-semibold text-sm hover:bg-gray-50 transition-colors"
            >
              G Pay
            </a>
          </div>

          <div className="px-6 py-1 text-center text-xs text-gray-400">or pay by card below</div>

          {/* Widget container */}
          <div className="p-6 pt-2">
            {status === "loading" && (
              <div className="flex items-center justify-center py-8">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"></div>
                <span className="ml-3 text-gray-500">Loading payment form...</span>
              </div>
            )}

            {status === "success" && (
              <div className="text-center py-8">
                <div className="text-5xl mb-4">✅</div>
                <h2 className="text-xl font-bold text-green-600 mb-2">Payment Complete</h2>
                <p className="text-gray-600">{message}</p>
              </div>
            )}

            {status === "error" && (
              <div className="text-center py-8">
                <div className="text-5xl mb-4">❌</div>
                <h2 className="text-xl font-bold text-red-600 mb-2">Payment Failed</h2>
                <p className="text-gray-600">{message}</p>
              </div>
            )}

            <div
              id="sumup-card"
              style={{ display: status === "ready" || status === "processing" ? "block" : "none" }}
            />

            {status === "processing" && (
              <p className="text-center text-sm text-gray-500 mt-4">{message}</p>
            )}
          </div>

          {/* Footer */}
          <div className="border-t px-6 py-4 text-center text-xs text-gray-400">
            Secure payment powered by SumUp
          </div>
        </div>
      </div>
    </div>
  );
};

export default Pay;
